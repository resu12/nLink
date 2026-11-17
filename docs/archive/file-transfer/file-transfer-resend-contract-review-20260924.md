# Second-Tuna resend contract: review and bounded fix plan

2026-09-24. HEAD `5f7a13dc047fa50da0702713db92b0903cb9e7c0`, branch `v0.7.0`,
plus the previously verified no-peer supersession changes. This step reviews the
contract and runs a synthetic predicate probe. No runtime/analyzer/test-suite
source, acceptance rule, historical result, commit or push is changed.

## Decision

Do not waive the failed live run or widen the pre-rejection resend window.
The review found an independently reproducible **false recovery match** in the
existing strict matcher: a Tuna receipt from another writer or publication can
satisfy its receive requirement. Correct that binding before considering a new
acceptance policy. This is an analyzer-validation defect, not a demonstrated
runtime routing or delivery defect, and not the cause of the 19 unresolved live
cases identified by the original primary.

The next bounded implementation should strengthen the existing second-Tuna
evidence selection, not implement the parked stale-safety classifier or make a
rejected packet grant authority. A successful fix may expose more unresolved
evidence; it is not expected to make the existing live failure disappear.

## What the current result actually means

`Get-FileTransferFileTunaV4DataPathMismatchRejectedFailClosedResult` requires an
earlier Regular-v4 source send, a matched rejection chain and a receiver Tuna
transition matching the rejected receive's local route tuple. For a transition
from fallback, it also searches for same-range Tuna send and receive records
between the original source and the rejection in retained input order.

`Sequence` is assigned as input lines are read (`LogParsing.ps1:148-175`). It is
not itself a cross-writer causal clock. The existing window remains part of the
legacy predicate; do not describe it as complete peer-lineage proof.

The latest sealed witness still has its later Tuna send at line 28955, after the
rejection at 28711. Later fallback dispatch and final SHA do not discharge that
predicate. No historical matcher or authority evaluator was rerun in this review.

The production guard captures a coordinator-issued publication and returns before
`DeliverFileTransferDataFrame` when regular NKN payload is disallowed under Tuna.
That path does not wait for a replacement resend or initiate one from rejection.
Forcing such ordering or retry effects from this callback would be a separate
runtime change and conflict with rejection being diagnostic-only.

## Reproduced binding defect

The payload index key contains session, transfer, frame type, range, byte count
and chunk count. It does not contain writer or publication identity
(`DataPathRejectedIngressEvidence.ps1:60-95,169-210`). The second-Tuna candidate
filters (`:881-918`) check that key, transport, protocol and the broad input-order
window, but omit the selected receiver writer/publication and receiver-local
ordering. The resend filter likewise omits the matched source writer.

`artifacts/resend-contract-review-20260924/Probe-ReceiptBinding.ps1` invokes only
the existing parser and unchanged matcher on explicit synthetic records. It is
not generated Coordinator authority evidence and reads no historical input.
PowerShell 5.1.26100.9549 and 7.6.6 agree on all nine cases:

| Synthetic case | Current matcher result |
| --- | --- |
| Correct writer, current tuple and publication-before-receipt ordering | `Recovered=true` |
| Tuna receipt from a foreign receiver writer | **`Recovered=true`** |
| Tuna receipt with an earlier epoch/runtime/leg/publication/commit tuple | **`Recovered=true`** |
| Tuna receipt before the selected receiver transition in both input and receiver order | **`Recovered=true`** |
| Tuna resend from a foreign sender writer | **`Recovered=true`** |
| Wrong receipt range or missing receipt | `missing_exact_tuna_receive` |
| Resend after rejection | `missing_tuna_resend` |
| Receipt after rejection | `missing_exact_tuna_receive` |

The four highlighted cases must not be used as exact evidence for the selected
source/receiver transition. The positive and missing/late/wrong-range controls
show that this is specifically an omitted binding, not a disabled matcher.
`Get-FileTransferFileTunaV4DataPathMismatchProof` consumes this Boolean and removes
recovered records from effective mismatches (`ArtifactWriters.ps1:14489-14509`),
so the omission can affect proof accounting. This probe does not establish that
any of the live primary's 43 recovered cases used this omission, or that a whole
scenario could pass its other independent gates.

There is a related fixture assumption to repair, not preserve as authority:
`AnalyzeRetained_SecondTunaCycleLateRegularDuplicateRejectedFailClosed_DoesNotFail`
calls the shared builder with `physicalRouteOrder=false`. That builder emits the
Tuna receipt at receiver sequence 8 before the selected transition at sequence 9.
The diagnostic matrix already opts into `physicalRouteOrder=true` and asserts
publication-before-receipt order. Retain the former ordering as an explicit
negative control; use the physically ordered setup for the intended positive.

## Minimal next implementation contract

Only the `post_tuna_fallback_v6` to `file_tuna_v4` branch of the shared matcher
and its directly affected tests are in this proposed slice:

1. Bind a qualifying Tuna resend to the already matched Regular source writer.
   Require positive writer sequences and source-before-resend within that writer,
   as well as the existing retained-input window and range/transport predicates.
2. Bind a qualifying Tuna receipt to the selected receiver writer and the exact
   local route tuple already checked between its transition and rejection:
   route, protocol, live epoch, runtime, terminal, leg, publication and commit
   generations, plus consistent Tuna target fields. Require positive local
   sequences with transition < Tuna receipt < rejected receive. Keep the existing
   retained-input window; require the receipt to follow that selected transition
   there too. Missing or contradictory required fields cannot qualify.
3. Never compare sender-local generation numbers with receiver-local numbers.
   Those are different authority domains. This change does not establish the
   sender's complete second-cycle publication lineage or authenticate a one-to-one
   send/receive pairing. Do not advertise it as the parked stale-safety proof.
4. Preserve first-activation behavior, the matched rejection-chain requirements,
   terminal/ambiguity checks, reason/variant/explanation mapping, duplicate
   handling, input immutability and exact range/byte/count checks. A valid exact
   candidate may still qualify alongside an unrelated nonmatching record.
   Update accounting only where the corrected predicate changes recovery.
5. Add the four reproductions to the existing diagnostic matrix, plus missing
   writer/tuple fields, incorrect receiver-local order and a positive case with
   different sender/receiver generation values. Run the matrix red-to-green on
   both PowerShell hosts, then the two full/compact second-cycle fixture cases
   and directly affected generated publication/payload diagnostic tests. Preserve
   original-primary-before-optional-companion behavior and full/compact/shadow
   parity. No generic aggregate suite, live run or historical reclassification.

If implementation requires a new cross-peer authority join, new DTO, production
capture or change to the temporal window, stop: that is outside this fix contract.
Do not redesign the payload index; apply exact predicates to its selected bucket.

## Acceptance-policy boundary

Three obligations remain separate: rejection before dispatch, correct use of each
coordinator-published route, and delivery/completion. The current strict recovery
predicate is not a general test that every rejected packet caused loss. Conversely,
observed rejection or final SHA does not establish validated stale-source lineage
or the required second coordinator-published Tuna route.

Current security capture records exact encrypted-payload/establishment references
and local installation lifetimes. The exported local publication carrier still
requires `scope=local_publication_forwarding` and `peerLineageStatus=not_evaluated`.
The September 21 Service fixture's flush/reopen assertion remains test-only
durable range evidence, not a production durable-write receipt. These improvements
do not supply the complete cross-writer proof required by the parked proposal.

An eventual alternative policy must separately specify and validate exact source
authorization, security lifetime, receiver guard/rejection, peer publication
lineage and independent route/delivery obligations. No positive exemption is
approved here. The useful immediate step is the bounded matcher-binding fix above,
not another reporting-only clarification or a live rerun seeking favorable order.

## Verification and preservation

All 16 pins from `artifacts/no-peer-supersession-fix-20260924/FINAL-VERIFICATION.json`
remain unchanged, including its 21 passing test receipts and the sealed live input.
That seal SHA-256 is
`f2ccf0644adabb8881ec093b12638f7c735adb0c299e663159799219176c039d`.
The prior live verdict remains FAIL, with its original 62/43/19 distinct/recovered/
unresolved accounting. This review adds only this document and ignored diagnostic
artifacts; it does not modify the already verified implementation or its seals.
