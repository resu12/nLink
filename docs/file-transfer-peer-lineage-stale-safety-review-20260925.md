# Remaining reciprocal-yield and stale-safety review

2026-09-25. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, including the existing uncommitted
Service-yield capture, fixture and selected-direction consumer. This step is
review/documentation only. It does not reopen the historical classifier proposal
as an implementation plan, change acceptance or authorize another live run.

## Decision

The next missing consumer edge is the reciprocal authenticated yield-to-receipt
association. Its original evidence already exists in the sealed two-writer run;
no additional runtime capture is indicated. Extend only the default-off test
consumer, then exercise genuine single-copy and receiver-local-first histories.
Do not infer that completing those checks proves general stale safety or resolves
the original `missing_exact_tuna_receive` result.

| Claim | Current evidence | Remaining limit |
| --- | --- | --- |
| Selected source-to-receiver yield reached its own accepted Coordinator receipt | Current custody-owned matcher, receiver pairing and exact second-Tuna handoff; full/compact agreement | One direction only |
| Reciprocal yield reached the source's own accepted receipt and paired release | Original send/ingress/association records and validated boundaries are present | Current matcher does not consume this chain |
| Single-copy and receiver-local-first matching works on real histories | Detached structural query controls only | No genuine original-writer integration for these variants |
| Held Regular payload was rejected without delivery in the fixture | Adapter guard checks plus surrounding Service state/progress checks | Bounded producer behavior, not a general safety verdict |
| Earlier exact-Tuna delivery obligation passed | Original primary remains unrecovered | Receipt lineage cannot supply missing payload receive evidence |

## Saved evidence reviewed, not reevaluated

The accepted run is
`artifacts/publication-service-yield-consumer-20260924/65cee6e86ba149888f2b418806cbd9ba/`.
Both hosts retain 383 raw records, 14 yield diagnostics, 339 primary full records,
162 compact authority records, 71 passing boundaries per view and three original
evaluations. The separate consumer reports `matched_original_yield_receipts`,
12 original witness origins and unchanged inputs. These are saved results, not
new tests or authority evaluations in this review.

The original diagnostic records identify the reciprocal candidate directly:

| Original writer file | Writer sequence / physical line | Observation |
| --- | --- | --- |
| `sender.log` | 68, 69; 74 | Forward control/bulk copies; accepted local association |
| `nlink.log` | 125, 129; 135, 149 | Forward authenticated ingresses; accepted and idempotent peer associations |
| `nlink.log` | 151, 152; 161 | Reciprocal control/bulk copies; accepted local association |
| `sender.log` | 86, 90; 94, 100 | Reciprocal authenticated ingresses; accepted and idempotent peer associations |

These are two independent writer orders, not a merged causal timeline. The
reciprocal canonical proof is `fte1-4d81bacbcc35e8bf1d3a293a`. Its receiver-local
association names boundary `fte1-e14914a62c07b42e2ea55c01`, the receiver pairing
already selected by the forward consumer. Its source peer association names
`fte1-7a80115af6488ee28171ff30`. The saved full view records the latter as a passing
`ApplyFallbackTailPeerProofReceipt`, `LocalProofObserved -> Paired`, with validated
local, peer and paired-release receipts in the source's existing yield operation.

The source association is logged at sequence 94 but its Snapshot at 103. Exact
returned-envelope equality remains required; Snapshot submission order must not
be substituted for receipt causality. The original source and receiver local
data-session generations remain 17 and 1, respectively, not equal peer counters.

## Bounded next implementation contract

Reuse the existing private primary observer, custody, original arrays/origins,
completed full/compact views and verified writer reader. Do not read saved logs
or summaries inside the consumer, export capabilities, reopen completed input or
perform another authority evaluation. Saved-artifact inspection above establishes
feasibility only; it cannot itself supply a positive consumer result.

1. Preserve the existing forward result and its exact source/receiver publication
   and handoff checks. Start the reverse query from the receiver yield operation
   already selected by that chain, not from a broad search for any later local
   association with the same transfer.
2. Bind that operation's original accepted, state-changing local association to
   its own independently successful control or bulk send slot. Reuse the current
   strict shape, security, scope, copy-outcome and duplicate checks. Follow the
   exact authenticated envelope to the source ingress and only that ingress's
   accepted peer association. An idempotent copy cannot borrow another copy's
   acceptance or successful send outcome.
3. Validate the named original source boundary, operation and receipt. Follow
   its complete applicable history to the source paired release. Its retained
   local proof must be the forward chain's original source local proof; its peer
   proof must reference the reciprocal receiver local proof, fingerprint and
   explicit remote authority/generation. Require unchanged writer, Coordinator,
   scope, local operation ownership and validated receipt continuity. Check
   invalidation and ambiguity in either direction; do not infer cross-peer
   lineage from equal owner labels, generation counters or timestamps.
4. Require the reverse local receipt to belong to the receiver pairing used by
   the forward chain, and the reverse peer receipt to belong to that same
   source yield episode. This closes two ends of the same exchange, not two
   independently convenient candidate chains. Validate both paired releases as
   locally owned receipts; do not equate their authority or receipt identities
   across peers. Receipt identity is also not release authority identity.
5. Keep the forward receiver handoff into its selected second-Tuna publication
   exact. The reverse query ends at the source's matching paired release; do not
   invent a symmetric responder handoff on the sender or claim that its later
   driver-authored publication is a complete sender Service pump execution.
6. Derive the two-direction association independently from both original views
   and require normalized equality. Resolve every selected boundary and
   diagnostic to its exact original source-line origin, and recheck ownership
   and unchanged inputs before returning the separate diagnostic.

Keep the existing bounds: 4,096 full records, 256 boundaries per view, 64
operations per boundary, and **64 deduplicated original witness lines total for
both directions**, not 64 each. Preserve inherited native/input and cumulative
work limits. Overflow or conflicting evidence is explicit unproven/omitted,
never truncation or a broader search budget. No generic lineage framework is
needed; share only the matching logic that the two directions actually use.

The separate result may add a reciprocal match field and exact witnesses to the
existing diagnostic; it must not rename the result to `proven_stale_rejected` or
export a whole-lineage/safety certificate. Preserve the original carrier's
`peerLineageStatus=not_evaluated`, primary failure, explanation, completion,
candidate accounting and acceptance. Keep `ClassifierEnabled=false`,
`AcceptanceChanged=false` and `Live=false`. Consumer omission/failure must not
replace the primary result.

## Genuine fixture coverage and verification

Use the existing two-process fixture and actual Service send/receive evidence.
Its current receive helpers unconditionally await two copies. Adjust only this
test orchestration to await the configured actually delivered copies, using
explicit rendezvous rather than sleeps. Configure delivery/outcomes at the
existing fake transport boundary before sending; never delete original log rows,
rewrite a receipt/history, or fabricate a successful unsent slot.

Use four fixed cases, not a broad cross-product: the existing both-copy,
control-first/receiver-peer-first baseline; control-only/receiver-peer-first;
bulk-only/receiver-peer-first; and both-copy, bulk-first/receiver-local-first.
Here copy order means actual delivery and resulting receipt order, not log-sort
order. The last case must execute the receiver's genuine local-yield path before
applying its first peer proof. If that order needs a production scheduling change
or a manufactured receipt, stop and report the fixture limitation instead.

Add focused detached negatives for reverse-only omission/substitution, wrong
writer/Coordinator/security lifetime, foreign yield episode, borrowed idempotent
receipt, failed selected slot, broken local/peer receipt retention, invalidation,
missing or substituted paired release, and disagreement between the two ends.
Keep these labeled query controls, not authenticated histories. Exercise shared
witness-limit and custody omissions without acquiring a second capability.

Run the focused matcher integration on both PowerShell hosts. Each genuine case
must pass original full/compact authority checks with the same three original
evaluations, exact input ownership and unchanged strict missing-receive outcome.
Record actual record/boundary counts rather than requiring the baseline counts
for structurally different histories. Then run only directly affected original
forward-consumer and default-off preservation regressions. No aggregate suite,
portable rebuild, historical reanalysis, GUI matrix or live NKN run is required.

## Safety boundary: retain, do not overstate

`NknSignalingTransport.FileTransferTransportChannel.cs:4446` rejects at ingress
before `TryValidateAndTrackFileTransferDataFrame`, recovery observation and
dispatch-work creation. Its later guard at line 4552 also rejects a prepared
callback whose current publication has changed, before fallback-receive
recording, `dispatch_entered` logging and delivery. However, preparation may
already have performed tracking/recovery under an earlier publication. The
shared `rejected_before_dispatch` label does not distinguish these two timings.
Thus it does not universally mean "no transfer-related work since ingress."
This is a scope finding, not evidence that either guard is defective.

Existing tests already cover useful, distinct boundaries:

- `NknFileTransferTransportTests.TwoWriterIngress.cs:346` checks exact rejection
  companions, unchanged adapter state/dispatch list and no data-session receive
  during the controlled replay; a second identical delivery is deduplicated.
- `SessionFileTransferSecondTunaPublicationTests.cs:181` surrounds that callback
  with unchanged Service state fingerprint, sent-frame/hint counts, transitions,
  bytes and frontier assertions. These checks **are exercised** by the sealed
  Service-yield fixture; do not describe it as adapter-only coverage.
- That fixture sets `replayPostCommitCallbacks=false` and does not request final
  fallback delivery, so it has no destination-file hash assertion and does not
  exercise the separate post-commit callback replay block. The existing replay
  and durable-fallback fixtures remain separate evidence, not implicit coverage
  of this run or production durability receipts.
- `NknFileTransferTransportTests.DispatchRouteGuard.cs:52` prepares a genuine
  callback, changes publication, then measures unchanged state from its
  **dispatch-time** baseline and no queue entry/delivery. It does not assert that
  preparation under the earlier publication had no effect.

A later safety consumer would still need an exact original receive/mismatch/
ignore chain with recorded disposition, unique authenticated source identity,
complete candidate publication/security history, and its reviewed bounded
contradiction checks. Current receipt matching does not perform those checks.
Absence of effect logs, a timeout, aggregate Snapshot pass or paired receipts
cannot prove absence of all effects. Do not introduce a generic effect correlator
or a new runtime event merely to widen that claim.

Reassess the precise safety claim after the reciprocal/fixture work; preserve
these producer-boundary distinctions. Any classifier implementation or acceptance
exemption is a separate decision. Exact Tuna delivery, route-cycle completion,
integrity and terminal obligations remain independent. No live-256 readiness is
established by this review or by a future reciprocal-only match.

## Review verification

The previous implementation seal SHA-256 is
`fcda5deabd7a5bbef5d74d5a38d88dae4fa96d30246880773081880ca92b8d41`.
All 113 prior pins and all 44 pre-existing dirty-file contents are unchanged.
This step adds only this document and the ignored review seal at
`artifacts/peer-lineage-stale-safety-review-20260925/FINAL-VERIFICATION.json`.
No tests, evaluators, runtime changes, acceptance changes, package rebuild,
live test, commit or push were performed.
