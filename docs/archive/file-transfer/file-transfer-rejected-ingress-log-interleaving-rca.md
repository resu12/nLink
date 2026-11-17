# Rejected-ingress diagnostic interleaving: RCA and fix plan

2026-09-21. Base HEAD `07ede2c0e073b1e7c4f87f9b021f1573741f7f69`.
Status: bounded diagnostic-grouping implementation complete in the working tree.
Focused logging/transport checks pass 95/95; analyzer checks pass 7/7. No route
authority or analyzer change, new live run, or historical authority reevaluation.

## Finding

The latest targeted live-256 run completed the file with matching SHA and proved
the second coordinator-published Tuna route, but its finalized correctness verdict
is still FAIL: 60 distinct data-path candidates, 59 recovered, one unresolved.
Performance and provenance passed. Completion is not a waiver of the finding.

The remaining rejected frame's receive/mismatch/ignored records retain one
captured Tuna publication, but a same-receiver fallback transition is logged
between mismatch and ignored. The retained matcher explicitly refuses such a
split chain. Its first missing edge is therefore **an uninterrupted rejection
diagnostic chain**, not a missing second-Tuna resend or an observed wrong-route
dispatch. The producer emits three separately locked log calls for one decision.

Recommend making those three diagnostic writes non-interleavable under the
existing logger gate. Preserve the captured tuple, rejection behavior and every
existing analyzer predicate. Do not remove the intervening-transition veto.

## Evidence and custody

Source directory:
`artifacts/checkpoint-replay-live-20260921/live/20260921T023401Z/regular-v4-live-activation-off-on-off-256mb`.

- Acceptance run: `76a85cdd-420c-452e-b3b4-25e221db5b33`.
- Original completed primary attempt: `5f136c83b38b4b55a96f3e6d85f01e0e`.
- Input: `filetransfer-retained-log-slice.log`, 66918 lines, SHA256
  `12e6a7cf007af58ea378b7d70473987c08094c7901c480dc6680206507692b59`.
- Receipt SHA256:
  `d57ee463ccbdd0c51a21bee80800a8fa0940e1e5e5c256f22e3358e08aa835f8`.
- This review rehashed the input, receipt and all eleven receipt-bound outputs:
  exact match. Full/compact parity and writer integrity passed in that original
  primary; gaps and duplicate sequences were zero.
- At RCA time the clean HEAD matched the run's recorded runtime provenance.
  Subsequent diagnostic implementation is recorded below; the old capture is
  not evidence of its runtime behavior.

Sender writer: `p30872recbf43c416c5465b`.
Receiver writer: `p25212r2e3d983e3c554180`.

| Retained line | Writer sequence | Observation |
| --- | --- | --- |
| 2394 | Sender 3820 | Regular-v4 physical send, publication/runtime/leg 1, live epoch 0: chunks 1872..1874, 64512 bytes, three chunks. |
| 7467 | Receiver 3034 | Initial Regular-v4 to Tuna-v4 transition; publication/runtime/leg 2, live epoch 1, commit 1. |
| 10004 | Receiver 3809 | Tuna-v4 to fallback-v6 commit, target publication/runtime/leg 3, live epoch 2; reason `fallback_transition_empty_inbound_drain`. |
| 10007 | Receiver 3812 | Old regular-NKN envelope, captured Tuna publication 2; `receive_guard`, `rejected_before_dispatch`, `authority_effect=none`. Original first failure. |
| 10009 | Receiver 3813 | Exact mismatch companion with the same captured tuple and disposition. |
| 10011 | Receiver 3814 | `filetransfer_route_transitioned` projects the fallback route, publication 3. This is inside the mismatch-to-ignore window. |
| 10013 | Receiver 3815 | Coordinator effect for fallback target-runtime installation; not an effect attributed to the rejected payload. |
| 10015 | Receiver 3816 | Exact ignored-frame companion, still captured Tuna publication 2, rejected before dispatch with no authority effect. |

The send and triplet share envelope fingerprint
`ftenv1-a1e4fc2fcfac77baf6b05a4acd95c412111f807229c8a91d5a7d1a873db13fea`,
payload fingerprint
`ftpay1-ea5185cfb39644eb164d53ddc29b22f0fd060ecc23d5ed715d58e82ce188b01d`
and security sequence 632. Projected session/transfer:
`fte1-ba39235ba8cb6ad2f12434c9` / `fte1-518d2958c32c9d94ac33ed74`.

The original `filetransfer-local-publication-diagnostic.json` selects line 10007.
It binds the captured reference to receiver coordinator
`fte1-f6b09a50e122d2c961f1de85`, publication boundary
`fte1-78574f708318db0a5c1decdc`, authored/observed generation 17, receipt fingerprint
`fte1-b5162b066f422a7412bdc920`; publication/effect source lines are 7443/7477.
The raw mismatch/ignore carry that same reference. This is inspection of the
original diagnostic, not a new resolver invocation or independent certification
of all three records. Its `peerLineageStatus=not_evaluated` remains unchanged.

## Executable cause

In `tools/FileTransferSoak/DataPathRejectedIngressEvidence.ps1`:

1. `Get-FileTransferFileTunaV4DataPathRejectedReceiveChain` identifies the receive,
   exact mismatch and ignored companion, ordered by receiver writer sequence.
2. Its final route-boundary test (lines 661-672 at this HEAD) asks whether a
   same-writer/scope `filetransfer_route_transitioned` lies strictly between
   mismatch and ignored. Line 10011 satisfies it: `10009 < 10011 < 10015`.
3. The helper returns null. The caller (lines 715-722) reports
   `missing_rejection_companion`, leaving `RejectionChainMatched=false` and
   `Variant=(none)`. It has not yet reached source-send or activation matching.

This reason is inferred from the exact raw fields and current source, then
confirmed on a fresh synthetic fixture. The sealed primary reports the broader
`file_tuna_data_path_mismatch`; it does not export this specific null-return cause.

In `NknSignalingTransport.FileTransferTransportChannel.cs`,
`ShouldRejectFileTunaV4PayloadReceive` captures one inbound route/publication,
calls `LogFileTransferDataPathReceiveResult` (Info then Error), then separately
calls Warn for the ignored frame (lines 3027-3072). `LocalOperationalLog.Write`
holds its gate for each individual call, not across the triplet. A concurrent
service log can therefore occupy writer sequence 3814 between those calls.
No log loss, corrupt ordering, or route-tuple recapture is needed to explain it.

The guard returns before `TryValidateAndTrackFileTransferDataFrame` and dispatch
(transport lines 4402-4407). The evidence does not show this payload being
accepted under Tuna. Earlier session-security liveness observations are a
separate existing path, not proof of file-transfer dispatch or committed bytes.

The [capture contract](file-transfer-publication-use-capture-contract.md) expressly
requires one detached guard capture even when publication changes between log
calls. Do not relabel it to fallback or infer fresh authority from its cached
observed generation. The earlier fallback commit log does not establish when
the guard captured its tuple relative to that commit. No cross-peer continuity
or exact committed receive-frontier finding is needed to identify this first
failed predicate; those have not been newly adjudicated here.

## Focused reproduction performed

`artifacts/checkpoint-replay-live-20260921/Inspect-RejectionChainSynthetic.ps1`
constructs invented session/transfer/writer records in memory. It imports only
the existing parser and shared matcher; it never reads the live capture or
evaluates Snapshot authority. Both Windows PowerShell and PowerShell 7 passed
all six cases for both receive and explicit-mismatch candidates: 12 assertions
per host, 24 total.

| Fresh synthetic arrangement | Existing retained matcher |
| --- | --- |
| Contiguous rejection triplet | Recovered |
| Same-writer fallback transition between mismatch and ignore | Unresolved; missing rejection companion |
| Same transition after the triplet | Recovered |
| Same transition before the triplet | Recovered under existing historical activation matching |
| Foreign-writer transition inside the triplet | Recovered |
| Terminal inside the triplet | Unresolved; missing rejection companion |

These were matcher characterizations, not a generated runtime concurrency test
or retroactive recovery of the saved live witness. GUI and retained policy
differences documented in the shared-ingress results remain intact.

## Bounded implementation plan (completed offline)

### 1. Pin the producer regression

Extend the existing publication-use transport fixture with controlled concurrent
route logging during rejection. Check that rejection still returns before
file-transfer state mutation/dispatch, and that all three diagnostics preserve
the same frame/security/publication capture even if the accepted route changes.
Use deterministic synchronization, not sleeps or a probabilistic loop as the
sole regression. Demonstrate the split with the old emission path first.

### 2. Group only this diagnostic triplet

Add a small internal logger entry point for a fixed, already-materialized group
of three entries. Hold the existing `LocalOperationalLog` gate across their
normal writes. Retain each entry's Info/Error/Warn level, source, redaction,
timestamp and existing per-write sequence/batch/receipt bookkeeping. The Core
assembly already grants `nLink.Infra.Nkn` internal access.

Do not introduce a new durable transaction, combined receipt, public schema,
raw multiline/header concatenation, callback-under-lock API, or new logger
queue. Reuse the normal write path, with the narrow enclosing gate; preserve
best-effort/non-throwing logging and existing failure markers. Contiguous write
attempts do not imply successful persistence: partial writes still fail evidence
integrity and must never be repaired or reclassified by the helper.

In the rejection branch, prepare all three existing messages from the one
detached capture and submit that group. Keep the delivery trace exactly once.
Leave successful receives and send diagnostics unchanged. Avoid duplicating the
long message builders; extract only formatting directly needed by this branch.
Do not acquire a transport/service/Coordinator gate, resolve fresh authority,
run application callbacks, await, or make network calls while holding the logger
gate. Bound the group to three entries so unrelated logging is not serialized
for a transfer's lifetime.

The group is a **diagnostic linearization point**, not an atomic route operation.
A concurrent transition may log before or after it, and the saved tuple may be
historical. Never reorder/suppress the transition or label the capture globally
current. Preserve all three event names and their original field meanings.

### 3. Verify the unchanged proof contract

Run only directly affected checks during implementation:

- Logger group concurrency: a competing same-writer entry lands wholly before
  or after the triplet; writer sequences remain distinct/contiguous. Redaction,
  severity, exact-batch failure and drain/completion behavior stay intact.
- Existing `PublicationUseEvidence` transport cases plus the new concurrency
  case: publication replacement/stale notification/missing evidence cannot
  relabel the guard or create dispatch, progress, repair or route effects.
- Fresh generated producer evidence through the unchanged retained matcher on
  both PowerShell hosts. Include missing source/transition/companion, duplicate
  or mismatched companions, terminal and deliberately split-chain negatives.
- Preserve second-Tuna exact resend **and** exact receive requirements. Neither
  grouping nor equal fingerprints can waive them. Preserve existing GUI policy
  and authoritative early-return behavior; do not activate the shadow adapter.
- Run the directly affected Core logging, Infra publication-capture and analyzer
  rejection-chain lanes once when stable; at most one broader relevant contract
  lane. No unrelated aggregate suites or Coordinator behavior changes.

Only after these gates pass: rebuild the portable package, verify current
provenance and strict bridge preflights outside the sandbox, and schedule the
single `regular-v4-live-activation-off-on-off-256mb` rerun with the required live
authorization. No full matrix. A fresh original primary must still prove the
second coordinator-published `file_tuna_v4` route; SHA alone is insufficient.

## Alternatives and limits

An analyzer exception for a matching captured-publication chain would change
acceptance semantics and need additional identity/authority negative controls.
Simply deleting the route-boundary veto would weaken an intentional guard.
Neither is necessary for the recommended producer-side correction. Similarly,
refreshing the tuple for the ignored line would violate the capture contract.

This proposal fixes the demonstrated diagnostic interleaving, not network
ordering, transport throughput or authority ownership. It does not promise that
the next live run has no later proof failure. The saved run remains FAIL, and
its input, summaries and receipt must never be rewritten or reevaluated to
demonstrate success.

## Implementation and verification

`LocalOperationalLog.InfoErrorWarn` is a fixed three-message internal helper
holding the existing logger gate across ordinary Info/Error/Warn writes. Each
write retains its original failure handling and independent sequence, batch,
timestamp, redaction and receipt machinery. No pump, sink, drain or receipt
implementation changed, and no application callback runs inside the new group.

The transport prepares the ignored message before writing the triplet, using
the same captured publication as receive and mismatch. The existing receive
formatter constructs each message once and calls the group only for rejection.
The successful-receive diagnostics, delivery-trace call count, guard decision,
state-change validation and dispatch paths retain their existing behavior.
There is no analyzer edit, new route token, authority lookup or retry policy.

The new transport regression uses authenticated receive security evidence and
an instrumented test-only segment collection. At each formatting boundary it
waits for a real competing-thread log write to finish; the first also publishes
the next Coordinator-issued fallback token. This deterministically broke the
old triplet at the ignored record (expected sequence 22, observed 23). With the
fix, all three rows are consecutive and retain the original capture; the
adapter still stores the later fallback publication, state count is unchanged,
and no dispatch record appears. No production synchronization hook was added.
This direct guard test complements the existing full ingress/dispatch tests;
it is not a new live transfer or coordinator route-cycle acceptance result.

The logger contention test covers 32 groups against 32 independent writes,
including per-entry severity, redaction, monotonic timestamps, distinct batch
identities and consecutive writer sequences. Existing failure, queue, drain,
receipt-custody and completion tests are included in the affected logging lane.

Final gates on a fresh Release test build:

- 95/95 affected Core logging and Infra publication-use/data-path provenance
  cases passed, including both new regressions and full encrypted-ingress cases.
- 7/7 affected analyzer cases passed: fresh generated full/compact proof parity,
  initial-activation guard matrix, second-Tuna recovery/negative matrix, shared
  GUI characterization/parity and authoritative early-return isolation. The
  applicable script fixtures run independently on PowerShell 5.1 and 7.
- `git diff --check` passed. Original live input, receipt and all eleven bound
  outputs remain byte-identical. No new live run, portable build, commit or push.

Detailed commands and harness corrections:
`artifacts/rejection-log-group-20260921/RESULTS.md`.

Next boundary: review/checkpoint the bounded diff as required for strict
provenance, rebuild the portable package, then obtain the required live-run
authorization and execute only the targeted live-256 scenario with strict
preflights. Offline gates are complete; the saved live verdict remains FAIL.
