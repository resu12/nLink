# Paired initial-Tuna readiness: reproduction and bounded fix plan

2026-09-21. Base HEAD: `bdfd45b964956867b02f3f00ae8b12981a8a270a`.
Status: deterministic reproduction and bounded contract complete; Core model
and service lifecycle/admission bridge tested, adapter fix still pending. See
`file-transfer-session-preparation-contract.md`.
The prior blank-line diagnostic edits are preserved independently.

## Reproduced first edges

`NknAccelerationTransportTests.InitialReadiness.cs` joins real
`NknSignalingTransport` instances and real `SessionFileTransferService` instances
over `FakeNknClient`, with the existing fake Tuna sessions. The host/helpee is
the file sender; the helper is the receiver. Only the selected payer can start
a paid listener. No route publication, readiness snapshot or runtime transaction
is preseeded for the handshake cases. The services retain real Coordinator
admission/publication decisions.

The network delivery gate retains a production answer or ACK while allowing
unrelated offer/decline messages to flow. Releasing a message delivers its
captured envelope; no readiness or authority token is synthesized.

| Controlled state | Helpee pays | Helper pays |
| --- | --- | --- |
| Ordinary session negotiation completed | Both providers ready; both Coordinators publish initial Tuna-v4 | Same |
| Ordinary answer ACK held | Sender ready, receiver unready; Tuna offer declined | Sender unready, receiver ready; both publish regular-v4 |
| `runtime_unlock` answer held | Sender unready, receiver ready; both publish regular-v4 | Sender ready, receiver unready; Tuna offer declined |
| `runtime_unlock` answer delivered before any transfer exists | Same asymmetry persists; payer answer acceptance requires an absent source publication | Same; receiver rejects `initial_tuna_readiness_missing` |
| Receiver evidence missing, expired, consumed for another transfer, or wrong-direction | Provider returns no evidence; Tuna offer declined, no inbound publication or runtime startup | Same |

The ordinary-session cases are positive/negative controls, not a claim that the
GUI currently uses that negotiation mode. The two runtime-unlock orderings
match the matrix's payer-role asymmetry. These are characterizations of current
behavior, not passing acceptance verdicts for the failed GUI scenarios.

## Root cause and evidence

1. `MarkInboundRuntimeUnlockOfferAccepted` in
   `src/nLink.Infra.Nkn/NknSignalingTransport.Acceleration.cs:26326` marks the
   dialer's transaction `AnswerReceived` with reason
   `transport_acceleration_answer_pending`, before the payer receives the
   answer. This local observation can satisfy the current GUI readiness wait.
2. The payer's received-answer path calls
   `TryAcceptActiveRuntimeUnlockInitialActivationAnswer` at line 30196.
   Its source-current guard at line 33394 requires a nondefault expected scope
   and a current `SourcePublicationAuthority` from an existing transfer.
3. A pre-transfer runtime unlock has no such publication. The new delivered-
   answer cases assert that the source publication is null, the guard is false,
   the payer remains `AnswerReceived=false`, and no answer continuation barrier
   is installed. Thus simply waiting for answer delivery does not close the gap.
4. `Wait-TunaGuiDiagnosticReadinessBeforeMeasuredTransfer` at
   `tools/GuiSmoke-Windows.ps1:33030` accepts one matching line from a combined
   log. It neither identifies both peers' current eligibility nor distinguishes
   the dialer's pending-answer observation from completed paired preparation.
5. The service correctly captures local readiness when selecting/admitting the
   offer; the Coordinator correctly rejects missing inbound evidence. The
   provider's successful snapshot read also consumes it for a transfer ID
   (`CreateInitialTunaReadinessEvidenceFromSnapshotUnsafe`, line 8240). It cannot
   be reused as an unscoped, supposedly read-only GUI polling API.

The latest live artifacts independently contain the same pending-answer marker:
helpee-payer slice line 419 and helper-payer slice line 341, each with
`reason=transport_acceleration_answer_pending` and a `peer-offer-dialer-1` lease.
The helper-payer original rejection remains at line 414. These artifacts were
read only; no historical authority evaluator or recovery predicate was rerun.

## Bounded next implementation contract

The pre-transfer design is now recorded in
`file-transfer-session-preparation-contract.md`. It uses explicit Core-owned
eligibility, isolated authenticated preparation message types, a first-admission
fence, and non-consuming paired observations. It preserves all of the following:

- Distinguish session-only preparation from activation of an already owned
  transfer. A missing adapter dictionary entry or default source scope must
  never authorize transfer-scoped activation. Define the Core-owned eligibility
  decision and its invalidation when a transfer/session lifecycle changes.
- Session preparation may negotiate a usable transport and retain bounded
  readiness evidence. It must not publish a file route, mark file Tuna active,
  unlock a file runtime, dispatch repair/retry, or manufacture a source
  publication. Actual transfer admission remains a fresh Coordinator decision.
- Preserve the existing source-current checks for active-transfer initial
  activation, installed retries, retired answers and late callbacks. Do not
  change the guard to accept an absent source publication.
- Provide a non-consuming diagnostic observation for the measured precondition,
  bound to the current session, peer/process, intended direction and local
  negotiation generation. Require both peer observations; never equate their
  independently allocated generation numbers. Such observations remain evidence,
  not authority, and cannot replace admission-time revalidation.
- Add races for transfer creation during preparation, stale/retired answers,
  session replacement, wrong peer/direction, expiry and consumed evidence. The
  existing strict route laws and missing-evidence rejection must remain intact.

Do not rename the measured preactivated scenario into a regular-to-Tuna cycle,
insert a hidden preparatory transfer, extend arbitrary sleeps, or change wallet,
queue/concurrency, retry cadence or throughput settings. The delivered-answer
reproduction shows why a longer GUI wait alone is insufficient.

The pure Core model and service-owned lifecycle/admission bridge are now
implemented. The latest service checkpoint verifies 26 new integration cases
and 123 focused regressions, including the prior model and readiness cases.
The distinct authenticated adapter wire boundary is also implemented.
Exact-generation listener/dialer
startup, ownership retention and cleanup are now implemented as a physical
prerequisite, with 42 new cases and 19 existing regressions passing. Core-bound
physical startup is now also implemented: 56 new cases and 28 affected regressions
pass, including immediate retirement and queued/awaited startup races. The
isolated three-leg handshake and preparation event isolation now pass 43 new
cases plus 151 existing regressions. Authenticated preparation offers obtain an
independent receiving Core permit; answer/ACK handling completes only readiness.
Exact lease observation/admission-time physical handoff now passes 37 new cases
and 138 existing regressions. Only the actual Coordinator publication can adopt
the rotated physical resource; old preparation cleanup cannot stop it.
Public purpose selection now also passes 30 new cases and 172 existing
regressions. Core selects pre-transfer preparation; existing transfer activation
retains its Coordinator publication/source guards. The paired diagnostic gate
now passes 35 new cases and 136 existing regressions: it requires fresh read-only
observations from both known GUI processes and leaves actual admission/publication
as separate requirements. Next: fresh portable, strict external-sandbox preflights,
and targeted initial-Tuna GUI validation. The shared lane stop API must still never be used for stale preparation
cleanup. No new live acceptance
claim is made. See the contract's adapter checkpoint and
`artifacts/session-preparation-wire-20260921/RESULTS.md` and
`artifacts/session-preparation-lane-20260921/RESULTS.md` and
`artifacts/session-preparation-execution-20260921/RESULTS.md` and
`artifacts/session-preparation-handshake-20260921/RESULTS.md` and
`artifacts/session-preparation-admission-20260921/RESULTS.md` and
`artifacts/session-preparation-request-20260921/RESULTS.md` and
`artifacts/session-preparation-diagnostics-20260921/RESULTS.md`.

After that bounded implementation: focused regressions and affected Core/Infra
lanes, a fresh portable build, then targeted GUI validation for the affected
initial-Tuna paths. No full matrix rerun yet. Live-256 exact-resend and regular
inbound-terminal proof gaps remain separate and unresolved.

## Targeted live follow-up

The fresh `active-tuna-v4-64mb` run at
`artifacts/session-preparation-live-20260921/live/20260921T115012Z` passed strict
global/scenario preflights and paired readiness, but not acceptance. The original
classifier rejected an inbound initial route-publication snapshot boundary and
the transfer made zero payload progress after manifest receipt. The next bounded
plan is in `file-transfer-initial-tuna-live-20260921-rca.md`; this supersedes the
pending-live next step above. Dirty-source provenance and finalized attempt
selection also fail. No further GUI run or production change was made.

## Verification

See `artifacts/paired-initial-readiness-20260921/RESULTS.md` for all runs,
including unsuccessful exploratory and sandbox-aborted runs. Sixteen distinct
reproduction cases are covered across focused successful runs, plus twelve
existing related contract regressions. No production code was changed in this
step; no live NKN/GUI test, portable build, commit or push was performed.
