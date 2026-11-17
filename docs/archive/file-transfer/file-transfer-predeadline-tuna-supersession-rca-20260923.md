# Recent-progress grace superseded before expiry: RCA and fix plan

2026-09-23. Inspected at `6eb328af1155373950705d30542e98357a1a08ae`,
branch `v0.7.0`. RCA only; no runtime or analyzer changes.

## Finding

The first missing proof edge is an unsupported **pre-deadline coordinator Tuna
supersession** in the recent-progress analyzer contract, not evidence that a
current fallback timer failed to fire. The receiver committed and published its
replacement `file_tuna_v4` owner before the old recent-progress deadline. The
old wake ran and correctly rejected itself as stale.

The existing recent-progress Tuna closure requires an expired recent grace,
`rechecked/stall_grace_armed`, a successor repair dispatch, and its retirement.
That describes a later handoff, not a handoff while recent grace is still open.
Do not make a retired fallback callback perform that sequence to satisfy the
analyzer. Do not classify supersession as successful repair or frontier progress.

This finding identifies a contract gap by inspecting retained records and source.
It is not a new acceptance verdict. A fresh deterministic reproduction and exact
snapshot-bound supersession contract are required before consumer implementation.

## Immutable evidence boundary

Use only the current-HEAD campaign:

`artifacts/tuna-setup-diagnostics-live-20260923-6eb328af/live/20260923T155549Z/second-transfer-after-reactivation`.

The original primary report selects retained-input line **13608**, writer
sequence **4816**, as
`filetransfer_fallback_frontier_recent_progress_grace_recheck_missing`.
It reports 33 raw records, four episodes, three recovered and one unresolved.
Its route-transition verdict is `pass`; overall correctness remains
`FAIL/incomplete_proof`. Primary analysis completed once, without timeout.

- Input: `filetransfer-retained-log-slice.log`, 46,380 lines.
- Input SHA-256: `09263ba1bf11abc6663505c969fd2b5bcf6465e65f387c322ada57aa2fd5ac2d`.
- Original final-pin manifest SHA-256:
  `d4daad2c252302c88ffba842e9b4b9ad81eef6386627f853227b9c47705fdf02`.
- Receiver writer: `p39216r8b7549e6d26e411e`.
- Bounded supporting selections and unchanged-input verification:
  `artifacts/frontier-recent-grace-rca-20260923`.

No historical primary classifier, snapshot validator, closure reducer or GUI
failure classifier was rerun. Supporting selections are read-only context for
the primary-selected failure, not newly selected acceptance failures.

## Exact timeline

Times below use the writer's high-resolution attempt ticks, except the explicit
captured committed-progress time and wake deadline. All lines refer to the
retained input above, not the differently numbered full GUI ledger.

| Line / writer sequence | Observation | UTC |
| --- | --- | --- |
| 13588 / 4796 | Repair at frontier 1010 settles `ProgressObserved` | 15:57:56.4942204 |
| 13590 / 4797 | Same repair records frontier 1010 to 1011 | 15:57:56.4950686 |
| 13592 / 4798 | Recent wake 69, frontier 1011, highest 1761 | 15:57:56.4955667 |
| 13596 / 4801; 13605 / 4812 | Exact repair progress forces receiver state 47; state publishes frontier 1011 | before the selected deferral |
| **13608 / 4816** | Primary-selected `frontier_stalled` deferral at 1011 | 15:57:56.5659051 |
| 13612, 13618, 13655, 13681 / 4820, 4827, 4872, 4884 | Wakes 70 through 73 advance only highest received, to 1773; deadline unchanged | before the transition |
| 13902 / 4969 | Exact `PostTunaFallbackV6ToTunaV4` transition committed | 15:57:57.0046287 |
| 13911 / 4974 | Coordinator route selection publishes `file_tuna_v4`, protocol 4 | 15:57:57.0095537 |
| 13941 / 4986 | Same transition disposition `Applied` | 15:57:57.0364215 |
| 13947 / 4989 | Same target's recovered-epoch publication | 15:57:57.0389026 |
| Captured wake deadline | Committed progress 15:57:56.4938661 plus one second | **15:57:57.4938661** |
| 14492 / 5081 | Wake 73: `stale/source_not_current`, `authority_effect=none` | 15:57:57.4959154 |

The committed transition precedes the captured deadline by **489.2374 ms**;
recovered-epoch publication precedes it by **454.9635 ms**. The latest callback
runs **2.0493 ms after** that deadline. Old wakes 69, 71, 70 and 72 report
`wake_generation_replaced` at lines 14486, 14488, 14490 and 14494.

The transition authority is
`fta2-filetransfer_route_transition-1a5c0f0035e20bd548b894b8`, generation 2.
Source route/protocol/epoch/runtime/leg/publication/commit are
`post_tuna_fallback_v6 / 6 / 1 / 2 / 2 / 2 / 0`; target values are
`file_tuna_v4 / 4 / 2 / 3 / 3 / 3 / 1`. Terminal generation stays 0.
The source data-session generation is 1; the captured checkpoint frontier is
1011, highest 1773 and durable checkpoint generation 1.

Both target projections carry coordinator evidence identity
`fte1-777914969d73e649f4bbd44c`, boundary
`fte1-f1a1608bd7153a9eceb5bf32`, publication state generation 177 and token
fingerprint `fte1-fe413dd1488f5e79da8997c7`. These are lookup witnesses, not a
substitute for the primary's validated snapshot. Redacted session/transfer text
alone must never establish identity across records or peers.

The legacy incident deadline is derived from the progress **log attempt** plus
one second (15:57:57.4950686), 1.2025 ms later than the captured deadline. That
difference does not explain this failure: both commit and publication precede
both bounds. Preserve the actual captured deadline in the proposed contract;
do not tune grace or slide a deadline using later progress.

## Source explanation

1. `SessionFileTransferService.PullTransferSessionV6.cs:8107` checks the immutable
   wake under the service gate. At line 8185 onward, a source that no longer has
   strict fallback runtime is rejected as `source_not_current`. The stale path
   logs and returns before `SendInboundV6FrontierRequestAsync`. Strict fallback
   requires the current post-Tuna fallback route, v6 runtime and current leg
   (`SessionFileTransferService.FallbackFrontierAuthority.cs:154`). This is the
   required stale-callback behavior, not a missing runtime action.
2. `ArtifactWriters.ps1:59901` first considers natural progress before expiry,
   then a current recheck/stall and successor repair. Its alternate closure
   calls are at 60009 and 60013. Neither alternate permits this pre-expiry
   replacement: `FileTransferFrontierGraceHandoff.ps1:1442` requires a current
   `rechecked/stall_grace_armed`; the Tuna helper at 2047 also requires that
   recheck, then a stall, successor grant/dispatch/send and retirement.
3. `FrontierGraceCorrelationIndex.ps1:290` already retains transition commit,
   disposition and route-publication events as broad support. The semantic
   prerequisite, not simply absent event names in the index, is the first
   identified gap. Partition/conflicting-copy completeness still needs tests.
4. The GUI recent-progress consumer at `GuiSmoke-Windows.ps1:16399` additionally
   gates its handoff helper on a repair-retirement diagnostic. That cannot
   describe a grace episode superseded before a successor repair is required.
5. Existing snapshot-authoritative supersession accounting is **stall-specific**:
   `FileTransferSupersessionAccounting.ps1` requires a settled-stalled repair
   and cross-peer handoff proof; the indexed merge at line 681 limits
   `Superseded` accounting to `Kind=stall`. A recent episode must not borrow
   that repair outcome, its boolean, or its stall admission.

## Bounded fix plan

### 1. Reproduce with fresh authority, then finalize the contract

Create a deterministic fixture using real Coordinator/service-issued source,
route-transition, publication and disposition receipts. Repair progress must
admit a recent episode; reactivation must complete before its captured deadline;
the old callback must remain diagnostic-only. Confirm that the present consumer
leaves this fresh episode unresolved. Do not feed the historical live capture
through another primary evaluation or edit it into a passing fixture.

Specify a separate `Superseded` disposition for this old-source obligation.
Keep admission and raw/distinct episode ownership unchanged; do not increment
`RecoveredCount`, claim successful repair, or manufacture successor repair
grant/dispatch/retirement evidence. Proposed accounting invariant:
`Distinct = Recovered + Superseded + Unresolved`.

### 2. Implement only exact, pre-deadline evidence closure

- Bind the original recent arm and complete same-deadline rearm chain to the
  admitted episode, data session, checkpoint, frontier and source scope. Select
  subjects before validation; do not skip a conflicting latest subject.
- Require one exact Coordinator-committed fallback-v6 to Tuna-v4 transition,
  its applied disposition and target publication, all completed strictly before
  the original captured deadline. Match source/target generations, checkpoint
  and data-session identity; reject replacement/ABA, pause, terminal, ambiguity,
  missing timing and contradictory same-writer copies. Distinguish durable
  checkpoint generation from separately logged source-leg checkpoint fields.
- Consume the **same primary's already validated snapshot** for authority,
  exact current-source/target boundaries and existing paired-peer/retry proof.
  Reuse the applicable exact target-proof rules, not the stall admission or its
  `SettledStalled` outcome. A route log, stale callback, listener/ACK, cached
  route, SHA OK or aggregate route-transition `pass` is insufficient.
- Keep stale callbacks diagnostic-only and unnecessary as positive authority.
  `source_not_current` alone never closes anything. A transition at/after the
  deadline must remain unresolved by this new branch.
- Thread already validated evidence through the recent consumer only where
  necessary. GUI/compact paths without that evidence must remain conservative;
  they may not create a second authority pass or silently accept log-only proof.
  Preserve one primary evaluation and its source-witness ownership.
- Add explicit recent `Superseded` accounting to directly affected merge/report
  consumers, with versioned accounting as needed. Do not hide a superseded
  episode inside the recovered count. Keep existing post-expiry repair/handoff,
  natural-progress and delayed-publication contracts unchanged.

Expected scope: the shared frontier/supersession evidence helpers, recent
consumer/index/GUI/report plumbing, and directly affected tests. No production
runtime policy, Coordinator authority grants, deadlines, retries, queue,
throughput, bridge setup or wallet changes.

### 3. Verify offline, then one targeted live gate

Focused negatives must include missing/rejected commit, disposition or
publication; missing/foreign snapshot or peer pairing; mismatched source,
target, checkpoint or data session; active Tuna-v6; changed generation/ABA;
paused/terminal source; conflicting writer copies including altered identity;
missing rearm link; moved deadline; late/equal-deadline publication; stale-only
diagnostics; and ambiguous duplicate transitions. Identical duplicate evidence
must not create new episodes. Preserve existing genuine missing-recheck failures.

Test flat/indexed, full/compact and applicable GUI accounting parity on
PowerShell 5.1 and 7, including narrow/multi-frontier partitions and missing
authoritative-evidence behavior. Run only directly affected tests, then the
relevant contract lane once after stabilization. Do not repeatedly run aggregates.

After review and offline proof, rebuild the portable and run only
`second-transfer-after-reactivation` with strict escalated preflights. Preserve
the old verdict; any improvement requires a fresh primary result. Stop at the
next first proof edge. Full matrix is not the next step.

## Limits and current status

The first transfer's SHA OK and this contract gap do not accept the whole run.
The second transfer still lacks its required Tuna publication, and enrichment
timed out; neither issue is explained away here. This RCA does not claim that
the previous runtime retry fix or the full matrix is validated.

Only this document and ignored RCA evidence were created. No tests, runtime or
analyzer edits, build, live run, historical reevaluation, commit or push occurred.
The verification artifact records all 143 original pins rechecked unchanged.
Next: fresh regression and bounded analyzer-contract implementation above.
