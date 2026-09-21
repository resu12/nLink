# Queued regular payload crossing a Tuna publication: RCA and bounded fix

2026-09-21. Baseline commit: `45123d93d67d3378b6e18cae0d957ce203e0a192`.

## Current evidence and failure boundary

The fresh portable built from that clean baseline ran exactly
`regular-v4-live-activation-off-on-off-256mb`. Its retained evidence is under
`artifacts/rejection-log-group-live-20260921/live/20260921T033047Z/regular-v4-live-activation-off-on-off-256mb/`.
The original primary completed normally, exit 0, with no timeout:

- Acceptance ID: `5a7f0f63-ab32-404a-8b35-348543c247a1`.
- Primary attempt: `0d1c09e012e74b388c07d308c2a668aa`.
- Input: 67252 lines, SHA256
  `88d47e6ea473b40a37259c8b91e2772568ae213df4b557a123d80de1d35de23a`.
- Receipt SHA256:
  `9bfd83d1e9558e1f63a7c91017973be2dc66a7a2d66081d75e9bb39f09c312a5`.

Correctness failed; performance, provenance, writer integrity and full/compact
parity passed. SHA matched and the second Coordinator-published Tuna route was
proved. The primary reported 116 distinct data-path evidence events: 65 recovered
and 51 unresolved. Those are not counts of corrupt files or unique payloads.
The retained input and all 11 receipt-bound outputs were rehashed read-only and
remain unchanged. No historical analyzer/authority reevaluation was performed.

The first remaining failure is `file_tuna_data_path_mismatch`, variant `(none)`:

| Retained line | Evidence |
| --- | --- |
| 2400 | Matching envelope/payload, security sequence 840, sent over regular NKN under the original regular-v4 route. |
| 12431 / 12446 | Receiver's second-Tuna Coordinator publication and corresponding effect, selected by the original primary diagnostic. |
| 12459 | Chunks 2499-2501 recorded as `dispatch_entered` over regular NKN, with the second-Tuna publication: epoch 3, runtime/publication generation 4, commit generation 2. |
| 12461 | Matching receive mismatch, receiver writer sequence 5397; receive row sequence is 5396. |

Receiver writer: `p11768rbe9215e14a784f00`. The primary-owned local-publication
diagnostic binds the publication/effect/use in both modes. Peer lineage is
explicitly not evaluated. This is not the previous run's split rejected-log
triplet: the new first witness says dispatch entered, not rejected.

## Root cause and deterministic reproduction

`TryPrepareFileTransferDataFrameDispatch` checks the current inbound publication
with `ShouldRejectFileTunaV4PayloadReceive` before transport state tracking and
creation of dispatch work. The callback can run later, after a route publication
changes. Previously it captured the current publication for diagnostics but did
not apply the transport rejection predicate again before data-session delivery.
Thus a frame admitted under regular NKN could enter the data-session queue under
a now-current Tuna-only payload route.

The regression establishes an authenticated fake-network transfer, prepares the
real callback under the original regular-v4 publication, holds that callback,
publishes real Coordinator-issued activation tokens, then invokes it. This
controls the scheduling boundary without sleeps or a production test hook.

On unchanged production code, three rejection cases failed at the actual queue
count assertion (expected zero, observed one): first Tuna, second Tuna, and
second Tuna without diagnostic publication evidence. Regular, fallback and
valid Tuna-delivery controls passed. This proves the producer-side gap can
deliver a payload into the transport receive queue; it is not only log wording.
The live rows do not timestamp the earlier guard or prove file-service progress
mutation, and this RCA does not attribute all 51 unresolved events to one cause.

The initial test-harness run failed before callback invocation because the
generic reflection helper searched the interface type instead of the concrete
data-session type. The corrected test uses the runtime type. Only
`dispatch-guard-red-reproduction.trx` is the valid pre-fix reproduction.

## Fix contract and implementation

1. Preserve the early ingress guard. It still rejects incompatible payload
   before ingress state tracking when Tuna is already current.
2. At the start of the prepared callback, use its existing exact inbound
   publication lookup once. Apply the same rejection predicate to that capture
   before fallback bookkeeping or delivery.
3. A rejected callback returns after the existing grouped receive/mismatch/ignore
   diagnostics. The group retains `receive_guard`, `rejected_before_dispatch`,
   `authority_effect=none`, security identity and the single captured tuple.
4. If not rejected, normal dispatch diagnostics use that same capture and retain
   `receive_dispatch` / `dispatch_entered`. No second authority sample is taken.

The implementation extracts the existing captured-authority predicate/emission
into a shared private helper and moves the callback's existing lookup before
fallback bookkeeping. It adds no authority source, route publication, runtime
unlock, retry, repair, progress mutation or scheduling policy. Missing diagnostic
evidence cannot disable the guard. Only existing Coordinator-issued publication
authority is consumed; observed transport and logging metadata grant nothing.

The dispatch capture is a rejection check, not a new permission for later work.
It does not hold the adapter lock across callbacks or delivery, invent a new
Coordinator admission token, or promise atomicity through the entire receive
pipeline. Earlier valid ingress preparation is not rolled back; the new rejected
dispatch performs no subsequent fallback bookkeeping or delivery. Separate
terminal/session/receive-service authority checks remain unchanged.

Do not instead keep an ingress-era tuple for dispatch logs, relabel a delivered
frame as rejected, weaken the analyzer, widen correlation windows, or tune
queues, throughput or retries. Those would hide or bypass the demonstrated edge.

## Verification and live boundary

The focused six-case regression passed after the fix. A seventh case checks that
regular-NKN control frames remain deliverable when Tuna is current. Rejection
assertions cover zero queued frames/bytes, unchanged dispatch-time transport
state, no dispatch diagnostic, exact publication/security identity, contiguous
rejection rows, and no replacement of the current publication.

Final gates passed: 56/56 directly affected runtime/Core/Infra cases, including
all seven new cases, and 3/3 focused analyzer cases (initial activation,
second-Tuna exact recovery/negatives, and freshly generated full/compact parity).
`git diff --check` passed. Detailed commands and results are recorded in
`artifacts/dispatch-route-guard-20260921/RESULTS.md`. No unrelated aggregate suite
or GUI matrix was run.

The original live run remains FAIL. Offline regression success does not establish
live route-cycle correctness. After review/checkpoint and a fresh portable build,
the next live boundary is the single targeted live-256 scenario with strict
preflights and the required second Coordinator Tuna publication. Do not launch
another live run from this implementation step.
