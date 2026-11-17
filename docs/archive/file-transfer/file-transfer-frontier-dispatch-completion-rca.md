# Frontier dispatch completion / reactivation race — 2026-09-13

## Evidence and first missing edge

The latest live run is `e56c76da-9ced-4815-be9b-732658b42be6`, scenario
`regular-v4-live-activation-off-on-off-256mb`, based on HEAD
`dad138284a470342245dc2249c19ca32076c6f12` plus the accumulated working tree.
The affected production paths had not changed since its portable build when
this RCA began. Its retained input under
`artifacts/filetransfer-route-acceptance/grace-recheck-live-20260913/20260913T114023Z`
has 102,310 lines and SHA-256
`21a5fa89b428ce823a2d612f6909a99fd8e0b717e17b166bf5965447829bb6cc`.

The fresh offline probe validates the **complete** authoritative Coordinator
snapshot before selecting the 4,547-event grace catalog. It reproduces stall
Raw/Distinct/Recovered/Superseded/Unresolved `37/37/36/0/1` (version 2), and
recent-progress `108/19/18/1`. Source supersession remains unproven; the
diagnostic is null, not an eligible `SourceSuperseded` result. The original
capture, reports and completed receipt remain unchanged. Probe outputs are in
`artifacts/frontier-7386-rca-20260913/initial-probe/`.

The first recent-progress candidate is retained line 74128, receiver writer
`p55104rc68ab451e30c440b`, sequence 46541, frontier 7386. Recent-progress wake 58
does recheck and arms stall wake 102. Accepted sparse writes renew it through
wake 120, keeping deadline `11:43:43.2258572Z`. The earlier renewal omission is
not the failure in this sequence.

| Retained line | Writer sequence | Exact evidence |
| --- | ---: | --- |
| 74967 | 47249 | Coordinator-issued repair `fta2-repair_replay-fd90bae1a4aa2a6b3fe69f72` granted for frontier 7386. |
| 75128 | 47303 | Transport reports accepted, peer-visible delivery for `v6-frontier:7386:12`. This alone is not authority. |
| 75310 | 47343 | Service records the delivery after Coordinator completion validation; snapshot sequence 47342 is `FallbackControlPlaneSendCompleted/Idempotent`, state 1838. |
| 75377 | 47357 | Coordinator commits Tuna-v4 reactivation, state 1839. |
| 75387 | 47364 | Repair is retired `StaleRejected/tuna_reactivation_replaced_fallback_owner`, still `dispatch_observed=0`. |
| 75441 | 47386 | Wake 120 returns `coalesced/exact_receiver_request_not_dispatched`. |

## Root cause

`SendInboundFallbackControlPlaneOrDataSessionAsync` validated and applied the
control-plane completion under the service lock. It then released the lock,
logged delivery, and performed pressure observation before returning to
`SendInboundAuthorizedFallbackFrontierRequestAsync`. Only that outer method's
second critical section validated the repair and recorded dispatch.

The handoff could commit in this gap. Its retirement correctly prevented the
late continuation from dispatching or changing the old contract, but the
already accepted delivery had never been atomically joined to the still-current
repair dispatch. Returning false then lost that accepted dispatch fact.

This is a split completion/application race. It is not evidence that transport
acceptance may bypass Coordinator validation, that late callbacks should mark
retired repairs, or that handoff must gain a new drain policy. The existing
supersession accounting contract does not classify this old capture as recovered.

## Bounded fix

Pass the exact existing repair contract only from the authorized inbound
frontier-request path into control-plane completion. After the Coordinator
accepts completion, validate that same contract, request/correlation alias,
transport epoch and data-session object while still holding the service lock.
Apply dispatch and emit delivery -> dispatch -> request-sent evidence before
releasing the lock. Reuse the same completion routine for data-session fallback.

The send result now describes the dispatch accepted at that critical section;
later source retirement cannot retroactively change it. The continuation may
log its captured result but cannot dispatch again, revive the source, mutate
progress, or create replacement work. Missing/replaced/settled contracts,
replaced data sessions and rejected Coordinator completions still fail closed.
Current repeated aliases retain their existing dispatch/coalescing behavior.

No Coordinator authority policy, route drain, retry/grace duration, bridge,
throughput, wallet, installer or GUI configuration changes. No analyzer admission,
recovery, supersession counting or evidence catalog changes. The prior highest-
received wake refresh is preserved.

## Verification and live boundary

The focused reproduction uses an actual service/Coordinator repair and a fake
control-plane transport. Post-delivery observation exposes the critical-section
gap deterministically. It also retires the exact contract before or after
completion; this unit fixture does not pretend to generate a real Tuna receipt.
Before the fix, two positive cases fail while stale-before-completion passes.

The implementation's seven focused cases pass, including settled contract,
data-session replacement, delivery-not-accepted and delivery-not-peer-visible
controls. The first test invocation encountered an external Avalonia telemetry
log permission failure; the scoped rerun uses `-p:UsedAvaloniaProducts=` to skip
only that telemetry target. A subsequent fixture-only correction seeds an
expired sparse stall before the actual reproduction. Failed invocations remain
in the ledger, not relabeled as runtime reproductions.

The 14 adjacent runtime cases pass: stall/recent-progress wake behavior,
checkpoint continuity, actual predecessor retirement during Tuna handoff,
receiver-state recovery deferral and same-frontier retry cadence. The affected
actual-emitter checkpoint and joined supersession consumer checks also run on
Windows PowerShell 5 and PowerShell 7.

That consumer lane exposed a stale checkpoint fixture, not another runtime
authority failure. Its manually constructed Infra notification omitted the
emitter's publication evidence, and long synthetic IDs were persisted as
`[redacted]:1`, which cannot bind the actual data-session identity in a typed
snapshot. The fixture now supplies the real emitter lookup and short synthetic
IDs, with assertions that the persisted identities are exact. Its forged typed
receipt control now explicitly requires snapshot failure and zero admitted
episodes in both full/compact retained and GUI consumers. Genuine recovery
still requires `1/1/1/0`; no production consumer rule was relaxed.

Adjacent verification and the final handoff are recorded in
`artifacts/frontier-7386-rca-20260913/RESULTS.md`. A fresh portable rebuild and
new live 256 MiB capture are required to validate the changed runtime ordering
and discover any next missing consumer edge. Stop before that workflow under
the current user boundary. Do not rewrite the old FAIL result or infer that
offline tests close the full live route-cycle contract. Dirty-source provenance
remains an independent acceptance gate; no automatic commit is included.
