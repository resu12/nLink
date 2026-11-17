# Fallback-yield evidence capture implementation

2026-09-24. Base HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`, branch
`v0.7.0`. Implements the producer/Service handoff from the approved
`file-transfer-yield-receipt-binding-review-20260924.md` contract. The complete
contract is **not yet closed**: the sealed two-writer Service-history integration
gate remains outstanding.

## Implemented

- Actual control-plane yield encryption retains one control and, when present,
  one bulk envelope capture. The original secure sequences/ciphertexts are used;
  retries reuse their original envelopes. Original queue/ACK/copy results and
  errors are logged separately. Non-yield frames retain capture-off behavior.
- A process-local diagnostic reference travels on the original delivery result,
  received-frame metadata and yield event. The queue and event carry the same
  authenticated ingress reference. No wire frame or authority DTO is changed.
- Local and peer receipt sites emit diagnostics after their existing state
  adoption. The actual returned Snapshot envelope supplies the projected
  Coordinator, transition, operation and receipt identities. Duplicate/rejected
  invocations use their own attempted-decision receipt, not an earlier accepted
  receipt. Missing/stale inputs do not acquire an accepted association.
- Captured transport security contexts remain attached to each reference. A reset
  or same-key reinstall can mark the original reference retired; no current-key
  lookup is used at receipt association.
- Canonical proof identity/fingerprint calculations are shared without changing
  their input normalization or hash. The existing Snapshot emitter performs the
  privacy projection. A narrowly scoped redaction rule retains only the new
  `ftye1` reference and existing security/envelope domains on the exact new event.

Every new record is `filetransfer_fallback_tail_yield_evidence`, version 1, with
`authority_effect=none`. `bound` is a local capture association, not proof of
complete authenticated peer lineage. Receipt decision, state-change flag and
receipt stage are separate from capture status.

The generic lifecycle sender, Coordinator implementation, authority/effect
catalogs, acceptance rules and classifier are unchanged. Full operational capture
already retains these ordinary diagnostic records; they are deliberately not
added to compact Snapshot authority families.

## Verification

`artifacts/yield-receipt-capture-20260924/results/capture-focused.trx`: 27/27 passed.
Coverage includes canonical hash/normalization, copy identity and original result
Booleans, control-only/bulk-only/both/queue-only/all-failed delivery, cancellation,
pre-send rejection, stale completion, original-context retirement, shared
authenticated queue/event handoff, strict redaction, and Service receipt handling.

`affected-regression.trx`: 48/48 passed, covering the directly affected security,
receipt/effect, control-plane and projection paths. The default-off preservation
check passed on both Windows PowerShell and PowerShell 7, with identical normalized
results and the original runtime-proof rejection preserved. Its capture is
`artifacts/publication-live-inputs-20260914/c09f46c491304c08a2220bbdfc0ebad9/`.

`final-focused.trx`: 13/13 passed on the final code. This reruns the receipt cases
after the final diagnostic refinements and adds an actual Coordinator rejection,
a missing-Snapshot/projection-failure negative control, and three existing queue
stress checks. The other legacy recovery-feedback queue check passed in
`final-boundary-regression.trx`. The three stress checks initially lost their
operational-log cursor under the default log destination; they pass with a
dedicated full capture under the workspace. No queue policy was changed. These
test counts overlap and must not be added as a unique test total.

Development attempts and their failures remain in the artifact directory. They
included the initial redaction/negotiation fixture gaps, fixture compile errors,
and a pending-versus-current peer-proof assertion corrected to follow Service
adoption. They are not live-network RCA evidence.

The Service fixture invokes `Record*FallbackTailLocalYieldLocked`, which dispatches
the production `Send*FallbackTailYieldProofAsync` path through the real NKN
control-plane adapter using fake clients. It does not manufacture a local proof
receipt or local-proof-sent effect. Both sender directions and both receipt orders
are exercised. Duplicate callbacks must leave the fallback substate unchanged.
Legacy, retired, invalid and stale metadata cases retain unbound, retired or
diagnostic-only status as appropriate without changing the normal receipt outcome.
Absent Snapshot evidence is distinguished from a callback that never reached a
Coordinator decision. A default decision value cannot falsely report a state
change when frame validation rejected the callback before receipt creation.

These focused fixtures use one test process with two fake transports. They are
**not** the contract's separate-writer positive integration gate. The earlier
peer-lineage prototype still has its pre-authored sender receipt and cannot serve
as that gate either.

## Remaining bounded step

Extend the sealed offline two-writer fixture to use a Service-owned sender and
carry the original receive metadata into the existing Service receiver. Preserve
both original writer histories, full/compact authority parity, original primary
evaluation count and normalized acceptance outcomes. Do not enable the peer-lineage
consumer or classifier as part of that fixture work.

No live 256 MiB run, historical reanalysis, portable rebuild, commit or push was
performed. The historical live FAIL is not changed by these producer diagnostics.
Earlier uncommitted prototype files and their historical seals are preserved.
