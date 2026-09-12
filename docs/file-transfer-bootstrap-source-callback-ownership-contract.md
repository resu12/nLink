# Bootstrap source-send callback ownership: approved bounded implementation

Status: the user approved the scoped commit in23.17 after the23.16 review found no actionable
blocker in the23.15 same-epoch recovery fix. Core retains exact committed source/capture
provenance and advances its publication linkage only through Core issuance. The three
callback install predicates share that proof without replacing ordinary runtime authority
checks. Recovery before/after cleanup passes. The scoped commit includes the accumulated
correlation dependencies but excludes unrelated frontier files. Production remains disabled;
no push, packaging, enablement or live/GUI acceptance is authorized by this commit gate.

## Implementation and verification result

Coordinator-issued callback identities now remain registered independently of physical
send settlement. Exact source-hold captures include pending callbacks even when physical
leases have retired. The actual sender/supervisor carries callback identity to final
effect authorization under the mutation lock. Held/stale completion does not use generic
stale-pump recovery; only an exact Coordinator-committed successor can restart source work.

The new explicit `source-callback-v1` evidence extension has catalog hash
`0ad879a93ec4d4639ecffd0fecf1c64d2aa32dc594bd39c680b8ca30c3c492dd`.
The frozen initiation profile/hash is unchanged. Callback capture/consumption markers
are diagnostic evidence and cannot satisfy physical route-effect checks.

Final verification: affected runtime lane PASS50; terminal/snapshot lane PASS24;
new callback reader PASS1 and initiation reader PASS4 on PowerShell5.1/7; explicit
profile redactor controls PASS2. The three original deterministic joined sender-only,
receiver-only and dual-stop scenarios PASS3. No deadlines/source release were changed.
Measured stack-boundary extractions preserve original statements and assertions; durable
reconstruction audits pass. Earlier failed/aborted candidates remain retained, not counted
as passing lanes. These23.9 results are historical;23.11 adds actual-supervisor handoff and
receive-gap regressions, stage/commit/reopen checks, retained-capture projection controls,
affected legacy installation checks, and fresh reader/joined verification. A cancellation
regression found during that lane was fixed and its four affected controls rerun successfully.
The final four new runtime cases and all three joined variants pass. The earlier65-case
invocation remains64 PASS/1 FAIL in retained evidence, not relabeled as a wholesale pass.
These bounded results alone did not authorize a commit or deployment/live acceptance;
the subsequent user approval in23.17 authorizes only the explicit scoped commit.

The23.13 extension issues a fingerprint-pinned internal callback retirement record only
from an accepted exact capture/drain and completed exchange/committed successor. It is
diagnostic classification, not runtime authority. The old callback stays registered and
retains its original capture until actual consumption; current/unbound work still fails
closed. Core controls PASS11, new/old cross-host reader controls pass, final actual-supervisor
orderings PASS2 and the three joined variants PASS3. The68-case affected invocation remains
67 PASS/1 fixture synchronization failure in its original evidence; the corrected handoff
cases were rerun without weakening assertions. Modeled successor evidence intentionally
remains unresolved for absent physical effect records. See23.13 for full retained provenance.

The23.15 recovery extension adds internal Core provenance, not a new wire/snapshot contract.
Final source-hold controls PASS33, callback binding/orderings PASS19, remaining publication
control PASS1 and joined/dual-host reader checks PASS5. The broader affected invocation
retains65 passes followed by a stack overflow, not a green aggregate. Passing already-
materialized commit values by readonly reference removes the new duplicate graph-copy
frames; the affected crashing path and binding controls pass on final source. Original
callback fingerprints/effects and physical-evidence rejection remain unchanged. See23.15.

## Proven defect

At checkpoint23.8, the current-source regression
`BootstrapSourceFailure_HoldAfterEligibilityCannotTerminalize` failed both cases in
`bootstrap-failure-race-owned-red-20260912.trx`. It runs the production physical-send
submission/failure path, pauses after its hold-aware eligibility check, applies an
actual Coordinator source-hold/capture through the Service, drains the physical work,
and invokes the production failure-to-terminal method with the original runtime owner.
Both cases wrongly terminalize the held transfer. No terminal authority is seeded.

The two cases distinguish physical work from callback work:

- Physical settlement pending: the exact nonzero send-lease ID is in the hold capture.
- Physical settlement complete: the lease observer has retired that exact send, so the
  physical capture is empty, but the failure callback is still paused and can terminalize.

This confirms the eligibility-to-terminalization gap suggested by the preceding
receiver-only joined failure. It is a controlled Service-path reproduction, not a new
joined-network acceptance run. The controlled trigger is a valid local-stop evaluation;
the natural receiver-only trace used peer-wake initiation. The eventual fix must cover
the real peer-wake path as well.

`SendChunkIndicesV4Async` checks hold-aware currentness under `gate`, then throws after
releasing it. `FailOutboundV4Async` receives the owner, message and optional pump install,
but not the exact send-callback identity. `TransitionOutboundToTerminalAsync` checks
that install, whose owner/publication intentionally remain unchanged while held.
`ObserveOutboundPhysicalSendLeaseAsync` owns physical drain proof only and removes a
lease when its physical completion task settles. Thus the existing capture cannot name
every pending callback. A captured-physical-lease-only guard would leave the second
case broken; a blanket ignore-while-paused guard would erase unrelated current failures.

The first fixture candidate lacked the required Tuna authority-session interface and
failed before establishing a nonzero physical lease. It is retained separately as
`bootstrap-failure-race-red-20260912.trx`, not counted as defect proof. The corrected
fixture adds that interface only on its dedicated recording-session subclass.

## Required authority boundary

Physical settlement is evidence that bytes/tasks have stopped; it is not permission
for a later callback to mutate source progress, pause/recovery state or terminal state.
The existing Coordinator-issued source-hold command and accepted exact capture must
own suppression of those callback effects. Bridge status, adapter dictionaries, empty
physical queues and an old owner-currentness result cannot supply that authority.

This approved representation extension goes beyond the physical-only capture in
contract8. An incomplete physical-work test or unbound Service flag cannot replace it.

## Approved implementation boundary

1. Track the lifetime of each admitted source-send callback separately from its physical
   completion. Reuse the send lease identity and its immutable issued source authority;
   retain exact Coordinator/agreement, owner/publication/commit/terminal/leg/transport/
   checkpoint scope, data-session reference/service/provider generations and pause
   instance. Register before submission, retire once after callback effects are consumed.
   This is ownership bookkeeping, not a new dispatch queue, retry loop or timer.

2. Extend the Coordinator source-hold capture with the exact pending outbound callback
   identities, including callbacks whose physical tasks already settled. Service reports
   that set under the same gate that closes source admission; Coordinator accepts and
   pins it against its issued command. Preserve the existing physical-drain receipt:
   do not wait for suppressed callback effects or reinterpret callback count-zero as
   physical settlement. No circular wait between failure handling and hold drainage.

3. Carry the callback identity through the actual sender/supervisor failure path to the
   final effect boundary. Check its exact current or captured-held ownership under the
   same lock as any terminal mutation. Distinguish current-unheld, held and stale outcomes.
   Held/stale outcomes are diagnostic-only: no terminal/progress/peer-error emission,
   pause/recovery/retry decision, checkpoint refresh, source reopen or pump installation.
   In particular, do not return a generic stale-failure boolean that triggers the existing
   `StartCurrentPumpInstall` recovery branch. Keep authenticated control receive alive;
   only the existing Coordinator-authorized fallback publication/handoff may start the
   successor source runtime. Preserve genuine current-unheld failures and independent
   user cancellation, disposal and source-read errors.

4. Add explicit versioned callback capture/effect linkage to the evidence contract.
   Do not silently change the frozen initiation profile/hash or use old physical receipt
   fields to claim callback ownership. Independent readers must reject missing, stale,
   duplicate-conflicting and wrongly attributed callback effects. No physical route
   acceptance is inferred from modeled callback evidence.

## Verification and stop conditions

First make the two retained regressions pass through the actual supervisor, without
directly substituting terminal calls. Force the real peer-wake arrival at the eligibility/
throw/final-authorization boundaries. Cover physical success/failure, callback before/
during/after capture, physically settled but callback pending, duplicate retired callback,
and owner/publication/terminal/session/provider/pause/agreement replacement. Assert no
forbidden side effects, not just survival. Keep positive current-unheld failure and user
cancellation controls.

Then run the directly affected Coordinator capture, Service callback and Infra joined
paths, followed by independent full/compact/shared-GUI reader controls. Rerun only the
three deterministic joined stop variants with their original source release and deadlines.
No full GUI matrix, package, production enablement, campaign/report, performance tuning,
new retry budget or commit is part of this contract. Stop for review if the exact callback
ownership cannot be expressed within this bounded source-hold lifecycle.
