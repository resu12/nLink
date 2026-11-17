# Real probe/ACK provenance: separate validation boundary

Implementation follow-up (2026-09-09): the subsequently authorized ordinary-offer
applicability correction passes its focused gates. The bounded real joined
continuation also passes; see
`artifacts/validation/consolidation-bounded-20260907/rca-remaining-stall/ordinary-offer-fix/RESULTS.md`
and `../joined-retry-continuation/RESULTS.md` relative to that result directory.
Neither result is a live transfer/route-cycle/finalization acceptance claim.
The original validation-only findings below remain preserved.

2026-09-09, base HEAD `b955354f`, existing worktree preserved. No production change,
build, live NKN run, portable/preflight, timeout extension or rerun was performed.

## What the existing joined fixture proves

`JoinedFallbackYield_ExactQueuedRequestInstallsOnlySenderRetryAuthority` passes:
real loopback peer-visible yield frames, opposite-peer pairing, the exact queued
request, Coordinator retry installation and consumed release are checked.

It deliberately ends at installation. `QueuedRetryAuthorityLoopbackTransport`
is explicitly first-install only (`SessionFileTransferRouteOverlapRetryBridge.cs:7`).
Its confirmation at183 retains the exact installed contract without starting an
offer, transaction, lease, probe or ACK exchange. The joined test at238–283 keeps
the fallback route unchanged and requires no route-commit or progress event.

Therefore the first missing joined proof is:

`Coordinator installed receipt → production retry offer/transaction/lease execution`

Separate standalone tests cannot be stitched together as evidence of that link.
The typed Core acceptance fixture supplies a probe and a test observer; the Infra
convergence fixture seeds transaction/lease/ACK state. They remain local controls,
not joined physical provenance.

## Standalone ACK prerequisite failed before answer emission

The bounded prerequisite lane is saved under
`artifacts/validation/consolidation-bounded-20260907/rca-remaining-stall/successful-confirmation/prerequisites-03.trx`:
three tests pass, one fails. Passing controls are the joined first-install test,
Core active-successor settlement, and Infra predecessor-cleanup gating.

`TransportAccelerationAnswerAck_DefersDialerFileTransferHandoffUntilAck` fails at
`NknAccelerationTransportTests.cs:21166`, its first6-second wait for local
availability plus a blocked outgoing ACK. It never reaches its ACK-provenance
assertions or the deliberately expected later probe-start timeout.

Original log:
`successful-confirmation/prerequisites-03/prerequisites.log` under the directory above.
SHA256: `4C71604E35B794BAE95A6A25C4CDA20A804CB4504BBDF4320771FBBD91684BDE`.
Writer: `p56028re56f84af15a5472c`.

| Writer sequence / line | Evidence |
|---|---|
|116|Offer received through FakeNknClient.|
|130|Dialer ready.|
|132|Offer trigger is `session_security_state_ready`.|
|135|Explicit `runtime_unlock` is queued behind the pending ordinary offer.|
|136|Lane becomes unavailable: `post_fallback_paired_retry_offer_binding_stale`.|
|137–141|Reset/resume diagnostics; no answer or semantic answer-ACK follows.|

This is not evidence of fake-network ACK loss. The ordinary offer takes the
non-runtime branch at `NknSignalingTransport.Acceleration.cs:29960`, with
`runtimeUnlockDialAttemptStarted=false` and no paired scope. After successful
dialing, the common path at30132 unconditionally calls
`MarkInboundRuntimeUnlockOfferAccepted`. Its applicability check at26325 returns
false for the non-runtime trigger and absent pair. The caller clears pending ACK
state, stops the lane, and returns before `SendAccelerationAnswerAsync` at30167.
The stale-pair reason does not prove a stale paired authority in this path.

Session/message identities in these logs are redacted. No missing exact peer
identity, probe observation or Coordinator authority is reconstructed from them.
This is a current deterministic simulated-transport test blocker; it is not
attributed to a historical live failure.

## Separate follow-up, after successful-confirmation evidence

1. Retain the failed prerequisite and reproduce its ordinary-offer applicability
   branch with controlled production negotiation inputs. Keep the distinction
   between ordinary offers, InitialActivation and exact paired PostFallback.
2. Review a narrow applicability guard at the call site: the runtime-unlock
   acceptance helper must remain mandatory for runtime/paired offers, but its
   `not applicable` result must not reject an otherwise valid ordinary offer.
   Do not broadly make the helper return true, invent a paired scope, suppress
   the ordinary negotiation, increase timeouts, or grant Coordinator authority.
3. Validate ordinary offer→answer→semantic ACK and strict missing/stale paired
   controls. Only after that prerequisite passes, join the real installed retry
   receipt to production negotiation and then to the actual probe/ACK callbacks.
4. Require retained offer/answer/probe identities and exact Core/Infra receipt
   lineage across that joined continuation. Keep sender commit, old-source
   rejection and receiver handoff blocked until those proofs actually exist.

These are recommendations, not fixes implemented by this validation. Stop here;
no transport or route acceptance is claimed.
