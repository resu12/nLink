# Second-Tuna resend proof: batch-shape RCA and fix plan

2026-10-01. Source checkpoint `7ddbe15c3b894cf2497207cc39a08189b7578cc0`
on `v0.7.0`. Scope: inspect the latest original evidence and reproduce the
selected predicate with synthetic controls. Runtime and acceptance are unchanged.

## Finding

The first strict acceptance failure is an exact-batch proof gap. The delayed
Regular packet contains chunk **10387 only** (21,504 bytes). A Tuna send/receive
pair contains **10387..10389** (64,512 bytes) before that packet is rejected.
The current matcher indexes by exact range, byte count and chunk count, so the
larger batch cannot qualify as its required resend.

This explains `second_tuna_rejected_regular_ingress_exact_tuna_resend_missing`
for the selected witness. It does not demonstrate a routing defect or establish
that accepting range containment is safe. The original verdict remains FAIL.

## Original run and custody

Run `20261001T105502Z`, scenario
`regular-v4-live-activation-off-on-off-256mb`, under
`artifacts/live-checkpoint-7ddbe15c-20261001`:

- Strict bridge preflight and isolation passed; one targeted attempt, GUI exit 0.
- Both sides completed 268,435,456 bytes with matching SHA256
  `8dc00e829350997adbed1ad4e87af5c2c56376242a3e920fcd3265612359cdc0`.
- Both sides published Regular-v4 -> Tuna-v4 -> fallback-v6 -> Tuna-v4 ->
  fallback-v6. Route consistency and live-route-epoch proof passed.
- Writer integrity and original full/compact parity passed. The post-drain
  rejection did not recur; its earlier producer remains unidentified.
- The original analyzer completed with unchanged input. Its selected failure is
  `file_tuna_data_path_mismatch`, retained line 72299, receiver sequence 38758.

All **99 original artifact pins** in `post-run-review.json` were reverified.
The retained input has 102,215 lines and SHA256
`fa9f63b11bd03a6652058a18432c749f14784536a422eb98862dc2d5c30f25ff`.
The compact summary SHA256 is
`0541a46ca4f3674361b29691fa573cbcd6b8befde466ccb3602114e45b889038`.
No retained analyzer or coordinator authority evaluation was rerun.

## Selected proof chain

Source: the original `filetransfer-retained-log-slice.log` in the scenario folder.
Sender writer: `p53000r98a74b872d0d4c45`.
Receiver writer: `p6448r783a16bdb70042f7`.

| Retained line | Writer sequence | Observation |
| --- | --- | --- |
| 55167 | Sender 45270 | Regular-v4 epoch 0 sends chunk 10387, 21,504 bytes, count 1. |
| 70436 | Sender 60600 | Tuna-v4 epoch 3 sends chunks 10387..10389, 64,512 bytes, count 3. |
| 70569 | Receiver 38149 | Coordinator-projected fallback-v6 -> second Tuna-v4 transition, epoch 3. |
| 71867 | Receiver 38381 | Matching three-chunk Tuna envelope received, `dispatch_entered`. |
| 72299 | Receiver 38758 | Old one-chunk Regular envelope rejected under Tuna-v4, `rejected_before_dispatch`, `authority_effect=none`. |
| 72301 / 72303 | Receiver 38759 / 38760 | Exact mismatch and ignored-frame companions for the old envelope. |

The Tuna pair shares envelope fingerprint
`ftenv1-81127a1aa497d0ea13c537d155005d4ce0ba75186c4fb24d6e9d087d8f5b1f5a`,
security sequence 6584 and encrypted-payload fingerprint
`ftpay1-f59e8002aedcd6c00b5e4949e6e7aaabf4084190bda78c7882009f14360b860c`.
The old Regular send/rejection instead share envelope fingerprint
`ftenv1-d8e52e7139f0c4ddcb99409b82434a8dbaef86b1fc2e5bf93f0a107e7471042b`
and payload fingerprint
`ftpay1-601701c980f49d23a9364ce8e2da101edb9515634553b5a379a53e1af8ab0033`.

On the receiver, transition, Tuna receipt and stale rejection have matching local
route tuples: epoch 3, runtime/leg/publication 4, terminal 0, commit 2, Tuna-v4
target. The receipt and rejection also reference the same receiver coordinator
publication boundary/token. Receiver-local order is 38149 < 38381 < 38758.
Sender and receiver generations are independent; equal numbers and merged file
order are not cross-peer causal proof.

Read-only inspection found eight send/receive records whose range contains
10387, including fallback traffic, but no exact single-chunk Tuna send before
the rejection. Full extracted fields and source pins are preserved in
`artifacts/second-tuna-batch-shape-rca-20261001/inspection.json`.

## First missing edge and controls

In `tools/FileTransferSoak/DataPathRejectedIngressEvidence.ps1`,
`Get-FileTransferFileTunaV4DataPathPayloadIdentityKey` includes session, transfer,
frame type, start, exclusive end, raw bytes and batch count. Thus the one-chunk
candidate and the three-chunk Tuna pair enter different index buckets. The
fallback-to-Tuna branch of
`Get-FileTransferFileTunaV4DataPathMismatchRejectedFailClosedResult` requires an
exact indexed Tuna resend and receipt, with further writer/local-publication
and ordering checks. The missing resend is the first failed edge, before those
later candidate checks can qualify this larger batch.

Six synthetic controls passed on each of Windows PowerShell 5.1.26100.9549 and
PowerShell 7.6.6: **12 checks total**. These call the existing parser/matcher,
not the live analyzer or coordinator evaluator.

| Synthetic input | Current result |
| --- | --- |
| Exact one-chunk send and receipt | Recovered |
| Containing three-chunk send and receipt | `missing_tuna_resend` |
| Only send widened | `missing_tuna_resend` |
| Only receipt widened | `missing_exact_tuna_receive` |
| Disjoint three-chunk send and receipt | `missing_tuna_resend` |
| Exact pair plus containing pair | Recovered |

All controls preserved their parsed input and matched the rejection companions.
Script and receipts: `Probe-BatchShape.ps1`, `probe-powershell.json` and
`probe-pwsh.json` in the same RCA artifact folder. Synthetic controls explain
predicate behavior; they do not certify this live run or replace a real-producer
regression test.

The runtime guard remains appropriate: in
`NknSignalingTransport.FileTransferTransportChannel.cs`,
`ShouldRejectFileTunaV4PayloadReceiveWithAuthority` uses the captured coordinator
route authority, records the rejection and returns before stateful dispatch.
No resend, route change or progress mutation should be driven by that rejection.

## Limits and separate diagnostics

The larger batch's matching send/receive fingerprints establish its own observed
identity, not that the old one-chunk payload equals its corresponding subrange.
`dispatch_entered` is not a durable-commit receipt. Final file SHA is a separate
completion check, not a substitute for either proof.

Only the first selected witness was investigated. The original summary has 33
recovered and 68 unresolved mismatch candidates; this RCA does not adjudicate
the other 67 unresolved candidates or predict a whole-scenario pass.

No optional local-publication diagnostic receipt was emitted. The parent log
also records `payload-wrapper-adoption-failed`, `stage=connect`,
`ObjectDisposedException`, `lifetime_cancelled=True`. That observation alone
does not establish why the optional receipt is absent or cause the strict
resend failure. `AnalyzerOrchestration.ps1` selects the primary failure before
optional publication completion; its result does not control that verdict.
`LocalPrimaryPublicationCompletion.cs` explicitly reports
`scope=local_publication_forwarding`, `peerLineageStatus=not_evaluated`.
Local forwarding must not be promoted to cross-peer delivery acceptance.

## Bounded fix plan

1. **Review the acceptance contract before changing it.** Decide whether it
   intentionally requires the original batch shape or may accept the same
   canonical chunk payload delivered inside a different Tuna batch. Recommend
   reviewing the latter; this RCA is not approval to adopt it. Keep the original
   exact-batch predicate and current verdict unchanged meanwhile.
2. **Specify evidence for any proposed batch-independent proof.** Require a
   matched Tuna send/receive pair, its per-peer coordinator publication scope,
   receiver-local transition/receipt/rejection order, and an explicit binding
   between the rejected payload and the delivered subrange. Determine whether
   existing evidence can establish that binding before proposing new metadata.
   Mere containment, equal local generations, a queue acceptance or final SHA
   cannot supply it. Reject holes, mismatched payloads, wrong writers/scopes,
   stale publications and receipts after rejection. Do not silently widen the
   legacy range comparison or its event window.
3. **After separate contract approval, implement only the approved proof edge.**
   First reproduce one-chunk -> three-chunk rebatching through the existing
   real-producer fixture. Preserve exact-match controls, the rejection's
   diagnostic-only behavior, primary-owned witness selection, full/compact
   parity and the single-authority-evaluation boundary. Run focused tests and
   directly affected lanes, with at most one broader contract lane.
4. **Then validate one fresh targeted live attempt.** Rebuild the portable
   package, run escalated strict preflights and this scenario only. Retain the
   original historical FAIL; do not reclassify it using a new rule. A new first
   proof edge requires its own bounded inspection before any full matrix.

Do not change runtime batching, chunk size, retry cadence, queues, throughput
limits, shutdown lifetime or fallback behavior to manufacture an exact resend.
No production edits, acceptance edits, live rerun, commit or push were performed
for this RCA.

Follow-up: the [rebatching contract review](file-transfer-rebatching-contract-review-20261001.md)
establishes that current encrypted-payload fingerprints cannot prove subrange
content equality and specifies the next test-only reproduction boundary.
