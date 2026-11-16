# Original rejection-chain consumer: bounded next step

2026-09-25. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, including the existing uncommitted
reciprocal-yield work. This is a review and implementation contract, not an
implemented consumer or a stale-safety/acceptance decision.

## Decision and current evidence

The first missing consumer edge is the exact original receive/mismatch/ignore
chain. Add that check only to the default-off test consumer. No new runtime
capture, semantic retention, authority evaluation or production classifier is
needed for this edge. Do not combine it with a general contradiction scanner.

The reciprocal implementation already matched both directions in four genuine
copy/order cases. Its seven accepted focused tests are saved evidence, not tests
rerun in this review. The original primary remains `missing_exact_tuna_receive`,
unrecovered; receipt matching does not discharge payload-delivery obligations.

The latest both-copy case is
`artifacts/publication-reciprocal-yield-20260925/735aca88e133487da0bb5fbf1938239b/`.
Its original records give a concrete candidate:

| Writer file | Writer sequence / physical line | Evidence |
| --- | --- | --- |
| `sender.log` | 23 | Regular-v4 physical send, chunks 12–13, 2,048 raw bytes |
| `nlink.log` | 189; 202 | Receiver publication boundary; its publication effect |
| `nlink.log` | 221 | Selected receive, explicitly rejected before dispatch |
| `nlink.log` | 222 | Receive-side Tuna/Regular mismatch |
| `nlink.log` | 223 | Ignore: `file_tuna_v4_payload_requires_tuna` |

The receiver writer is `p7540ra9a50d4ecdae4efd`. All three rejection records bind
boundary `fte1-cfd916115860b2ec4e2acecb`, observed state 33, publication generation
4, commit generation 2 and the same authenticated payload/security sequence 3.
Their outer and payload fingerprints are respectively
`ftenv1-6c26c8721c01f6f473e4614fd5ea9f158f258586a63cd92a3cdda482071d058c`
and `ftpay1-4bf8f763239a89ea80d0350583e2a66ae85e38a7f5934c97b032c2dd265c20da`.
These also match the original source send. Cross-writer numeric generations are
not identities and need not equal.

The current consumer parses send/receive and yield diagnostics, but does not
select the mismatch/ignore companions or compare payload range/byte fields.
The legacy rejected-receive matcher separately derives `ReceiveDisposition`
after a legacy chain match. That derived value cannot substitute for checking
the explicit recorded fields here. Leave the legacy evaluator unchanged.

## Smallest implementation contract

Reuse the existing private primary observer, exact failure object, original
arrays/origins, completed full/compact views, native ownership and verified
writer reader. Opt in explicitly and require the current reciprocal match.
Do not reread saved artifacts, reopen the consumed writer reader, acquire a
second capability, or invoke recovery/authority evaluation again.

1. Start from the primary-selected original receive and the unique source send
   already bound by the yield consumer. Preserve all existing authenticated
   identity, local security-episode, publication and reciprocal-receipt checks.
   Require equal canonical `chunk_index`, `start_chunk_index`,
   `last_end_chunk_exclusive`, `frame_chunk_count`, `batch_chunk_count`,
   `raw_payload_bytes` and `payload_bytes` between source and receive, with valid
   positive counts/bytes and a consistent nonempty range for the selected v4
   payload frame. Do not equate transport bytes or batch profiles across peers:
   the saved send has 2,402 transport bytes and `v4_default_21k`, whereas receive
   records zero transport bytes and `(none)`.
2. Select exactly the same receiver writer's next two sequence numbers. Require
   mismatch then ignore, unique original origins, no duplicate structured
   fields, and the exact expected event/reason values. The producer's
   `LocalOperationalLog.InfoErrorWarn` holds its writer gate across three
   ordinary write attempts, so these sequences must be consecutive. Each has
   its own persistence batch; do not require one shared batch identity or infer
   persistence success from the grouped call. A missing entry is unproven.
3. All three rows must explicitly record `receive_guard`, inbound direction,
   `rejected_before_dispatch`, `authority_effect=none`, captured security
   context, Regular actual transport and the same `file_tuna_v4`/protocol-4
   authority targeting Tuna. Require exact local writer, canonical scope,
   outer/payload/security identity, security installation and sequence,
   publication version/Coordinator/boundary/operation/token, authored/observed
   generations and route tuple. Resolve each companion's local forwarding
   against each original authority view and require the selected publication,
   rather than trusting equal text alone.
4. Receive and mismatch must agree on their numeric range/count/byte fields as
   well as identity. Ignore carries `chunk_index` but no numeric range/count/
   byte fields. Check its actual shared fields; do not invent absent fields or
   weaken checks on the two records that do contain them. Require receive's
   `delivery_result=received`, mismatch's `proof_direction=receive` and
   `mismatch_reason=file_tuna_v4_resolved_to_regular_nkn`, and ignore's
   `reason=file_tuna_v4_payload_requires_tuna`.
5. Require independently resolved full/compact normalized agreement and exact
   original source-line witnesses. Recheck unchanged inputs and custody before
   returning. Reuse the existing total caps: 4,096 full records, 256 boundaries
   per view, 64 operations per boundary and **64 deduplicated witness lines for
   the entire combined query**, with inherited cumulative time/work limits.
   Overflow, ambiguity, missing fields or custody failure is explicit unproven,
   not truncation or a search for a different convenient receive.

Return a separate `RejectionChainStatus=matched_original_rejection_chain` with
the original witness locations, or explicit unproven/omission reason. This means
only that the recorded chain is original and bound to the selected packet and
publication. Preserve existing yield results if this optional check fails. Do
not emit `proven_stale_rejected`, change the carrier's
`peerLineageStatus=not_evaluated`, or alter primary failure, counts, completion,
recovery or acceptance. Keep `ClassifierEnabled=false`,
`AcceptanceChanged=false` and `Live=false`.

## Contradiction and safety review: limits remain

Rejection companions and `filetransfer_data_frame_dispatched` are already in the
semantic event catalog. However, catalog membership is not a complete safety
window. In this saved receiver tail, sequence 226 is Snapshot stream completion,
229 is security-context retirement and 231 is writer drain. The latter two are
present in the raw writer but absent from the retained semantic slice. Neither
Snapshot completion nor writer drain is a Coordinator publication-retirement
receipt. Do not extend retention or claim runtime retirement from these records
as part of the chain-only step.

The existing writer lease verifies the declared file hash and line count at EOF;
membership checks verify contiguous writer sequences and original retained-row
membership. This establishes a bounded captured input, not publication retirement
or coverage of all possible effects. Its verification pass is one-shot; the
consumer must not attempt another raw-reader pass to obtain absent tail records.

A later dispatch-observation query needs an explicitly selected, owned interval
from publication start through a validated closure or clearly labeled verified
capture end. It must include observations after the ignore, retain same-writer
ordering, and handle missing/conflicting bindings conservatively. A same-payload
dispatch under the same publication may be a conflicting observation or an
ambiguous different attempt; without exact attempt-to-effect binding it does not
prove that this rejected callback dispatched. Absence of such logs likewise does
not prove absence of all effects. This review does not authorize a generic effect
correlator or select a broader safety verdict.

The two runtime guards also retain different scopes: ingress rejection precedes
tracking/recovery preparation, while queued-callback rejection occurs after
possible preparation under an earlier publication. Their shared label does not
distinguish those histories. Existing fixture assertions cover adapter and
Service state/progress around the controlled replay; they are not a general
absence-of-effects or durable-file certificate. No new runtime defect is
established by this review.

## Focused verification for the next implementation

Use one fresh genuine both-copy two-writer integration with the new opt-in on
both PowerShell hosts. Its original full/compact authority checks and parity must
pass, retain exactly the existing three evaluations, and keep the strict primary
failure unrecovered. Reuse existing fixture behavior; no manufactured originals,
new route scheduling or payload replay changes are required.

Add detached query controls for each missing/reordered/duplicate companion,
wrong writer/sequence, substituted authenticated or publication identity,
explicit disposition/effect mismatch, wrong reason, source/receive range or byte
disagreement, receive/mismatch disagreement, custody loss, parity disagreement
and shared witness overflow. Confirm valid sparse ignore fields are accepted.
These controls are not genuine runtime histories.

Run directly affected consumer tests and default-off preservation once after the
implementation stabilizes. Do not rerun all copy/order variants unless their
fixture or matching behavior changes; do not rerun aggregate lanes for this
additive diagnostic. No portable rebuild, GUI matrix or live-256 run is required
for this step. Reassess the next missing evidence edge after the bounded consumer
passes; classifier and acceptance changes remain separate decisions.

## Review verification

The previous implementation seal is
`artifacts/publication-reciprocal-yield-20260925/FINAL-VERIFICATION.json`, SHA-256
`be5977c7d0a66adbf66e05ffd91beeb3da9d7eced63a8e45cfcab9a0f8609316`.
All 142 prior pins and all 48 pre-existing dirty-file contents remain unchanged.
This step adds only this document and the ignored review seal at
`artifacts/rejection-chain-review-20260925/FINAL-VERIFICATION.json`.
No tests, evaluators, source changes, package build, live run, commit or push were
performed in this review.
