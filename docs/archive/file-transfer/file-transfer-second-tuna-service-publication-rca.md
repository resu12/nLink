# C2f: second-Tuna service publication and retirement-effect capture

2026-09-15. Continues C2e without a live run or historical reevaluation.

## Contract decision

Keep the existing exact-range Tuna resend **and** receive requirement unchanged.
Safe rejection of an old Regular envelope is not evidence of payload delivery.
Local publication witnesses also do not prove the cross-peer lineage required by
the reviewed publication consumer contract. C2a's verdict remains unchanged.

## Fresh deterministic evidence

The new receiver fixture runs the actual service from its initial Regular
publication through the receiver barrier, initial Tuna commit, fallback bootstrap,
paired yield, responder handoff and second Tuna commit at epoch 3. Admission,
preparation, drain, commit, disposition and compatibility projections are
Coordinator/service-owned. The peer starts from an explicitly seeded fallback
baseline, but its yield authority and serialized proof frame are produced by its
own service/Coordinator. Bridge probe/checkpoint evidence remains controlled.
This is not an NKN two-writer payload fixture or complete peer-lineage proof.

Six service cases cover the positive path, missing peer proof, missing snapshot
observation, wrong publication, wrong data-session generation and wrong transition
mode. The positive path repeats the old handoff and exact drain execution after
commit. Rejections preserve persistent state, state fingerprint, publication,
route hints and frame count. Existing `convergence_observed` diagnostic effects
are explicitly distinguished from runtime-authorizing effects.

Independent full/compact/shared-GUI evaluation of the generated receiver stream
is complete with passing artifact integrity and parity, but initially has four
semantic failures. The first three are `fallback_tail_yield_authority_invalid`
at quiescent yield, local proof application and second-Tuna commit. The fourth is
`runtime_unlock_responder_acked_probe_route_transition_handoff_missing` on a
rejected post-commit observation. Completion/parity is not semantic acceptance.

## First missing edge and bounded fix

The receiver really clears fallback recovery ownership, but its generated
Snapshot stream lacks `recovery_owner_retired_after_yield`. Both inbound
quiescent paths call `RecordInboundFallbackTailLocalYieldLocked` before clearing
the owner. That send may complete inline and adopt the local-proof receipt,
replacing `LastFallbackTailYieldSnapshotEnvelope`. The retirement effect later
looks up that mutable last envelope. Its exact binding helper correctly accepts
only an accepted `FallbackTailQuiescent` issuance, so it emits no effect when
given the later `ApplyFallbackTailLocalProofReceipt` envelope.

Capture the exact issuance envelope after successful quiescent authorization
and before invoking the proof send. Carry it to the retirement projection while
keeping the existing clear-after-success order. Do not relax the effect matcher,
rewrite records, manufacture a receipt, or restore the old envelope into mutable
context state. Preserve the existing terminal-clear path and its helper callers.
The outbound analogue is not established by the scoped receiver input and is
not silently changed in this fix.

Implementation found a second part of the same binding loss: the emitter's
operation/receipt lookup also overwrote the issuance location when the same
receipt appeared in later observations. Capturing the envelope alone restored
the effect but bound it to state 26 instead of its issuance state 23. The emitter
now keeps a dedicated first-issuance binding for the existing exact accepted
quiescent-yield shape, used only by the retirement effect. Other effect lookup
semantics remain unchanged. The index is local to the emitter and grows by at
most one entry per issued yield; no new wire/catalog/token or companion budget.

The fresh fixed stream clears all three fallback-yield semantic failures. The
clean-cycle and post-commit-replay inputs are evaluated separately: the clean
cycle must have zero failed boundaries, while the replay control must preserve
exactly the existing rejected-handoff failure. Neither the analyzer nor its
failure result is modified. A passing characterization test is not an all-pass
verdict for the replay input.

The rejected-handoff semantic failure is a separate next edge; do not suppress it
to obtain an all-pass stream. The two-writer service/payload integration and the
existing resend predicate remain downstream work. No new live test is authorized
or useful until the directly affected offline gates are satisfied.

Next: review why the state-neutral `ObserveRuntimeUnlockResponderPeerProbeHandoff`
record after epoch-3 commit requires a new handoff proof. Pin the exact retained
accepted handoff/commit/disposition and rejected observation in the fresh fixture,
including wrong-writer, wrong-scope and missing-receipt controls, before changing
that diagnostic validator. Do not count rejection safety as delivery recovery.

Development and focused results:
`artifacts/publication-second-tuna-service-20260915/RESULTS.md`.

Final verification: 15/15 selected tests passed, including the existing one- and
two-writer primary flows on both PowerShell hosts. The clean service cycle passes
all 40 semantic boundaries; the 42-boundary replay control preserves its one
rejected-handoff failure. No live run, commit or acceptance reclassification.

## C2g continuation

`file-transfer-second-tuna-rejected-handoff-rca.md` records the subsequent
analyzer-only fix: the exact post-fallback Coordinator Bind decision is now
recognized as the consumed observation's acceptance, with the full commit chain
still required. Fresh replay evidence passes all 42 boundaries and retains both
rejections. Three older shared-analyzer regression failures reproduce with the
original reducer and remain a separate unresolved gate; see that RCA and ledger.
