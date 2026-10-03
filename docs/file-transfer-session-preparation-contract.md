# Pre-transfer Tuna session preparation: bounded contract

2026-09-21. Reviewed against HEAD
`bdfd45b964956867b02f3f00ae8b12981a8a270a` and the current working tree.
Status: Core model, service lifecycle/admission bridge, authenticated wire
boundary, physical lane ownership, Core-bound execution and isolated three-leg
handshake/event handling, exact lease observation and admission-time physical
handoff are implemented and tested. Public runtime-unlock now selects the
isolated executor through Core eligibility; actual admission can claim its
evidence and publish through Core. The smoke-only paired GUI observation bridge
and first-transfer readiness gate are implemented and tested. A fresh targeted
live run passed paired readiness but failed snapshot route consistency and made
no payload progress. The initial-owner analyzer mismatch is now reproduced and
fixed deterministically. Core-owned initial-publication source binding now
resolves the reproduced commit-only feedback gate; joined tests prove initial
credit and a four-byte payload in all payer/direction combinations. Live payload
completion remains unproven. See `file-transfer-initial-tuna-live-20260921-rca.md`
for verification and the next targeted live checkpoint.
Preparation alone still supplies no file route authority.

## Objective and boundary

Close the reproduced pre-transfer `runtime_unlock` answer-acceptance gap without
relaxing transfer-scoped authority. The payer currently needs a source route
publication that cannot exist before the first transfer. The dialer's
`transport_acceleration_answer_pending` observation is not paired readiness.
See `file-transfer-paired-initial-readiness-20260921-rca.md` for reproduction and
completed test results.

The first implementation is deliberately limited to preparation **before the
first file-transfer admission attempt in a verified session incarnation**.
It is not a new post-terminal reactivation or fallback-recovery mechanism.
Once admission starts, preparation cannot reopen merely because a context is
terminal, reset, detached, or absent from an adapter dictionary. Existing
session-reactivation evidence and transfer activation paths remain separate.

Success means that both payer roles can finish a real, authenticated preparation
handshake, report non-consuming readiness on both peers, and let the actual
first transfer obtain fresh Coordinator-authorized `file_tuna_v4` publications.
Preparation alone must produce no file route publication or file runtime start.
This does not resolve the separate live-256 exact-resend or regular-transfer
inbound-terminal proof gaps.

## 1. Disjoint purpose and authority

| Purpose | Required Core basis | Permitted result |
| --- | --- | --- |
| Session preparation (new) | Explicit current pre-transfer preparation permit | Negotiate a file-capable transport; retain bounded readiness evidence |
| Initial activation (existing) | Current source publication and expected activation scope | Existing Coordinator-approved transfer transition |
| Installed retry (existing) | Current installed retry dispatch receipt and bound scope | Existing Coordinator-approved retry transition |

Keep the app's user action `RequestAccelerationNegotiationAsync("runtime_unlock")`.
Do not use a new trigger spelling to evade existing guards. At the transport
request boundary, ask Core to select eligibility before selecting a preparation
purpose. Preserve current post-fallback, installed-retry and route-transition
precedence. A busy or unavailable preparation decision is not permission to run
initial activation with a default source scope.

`IsFileTransferRuntimeUnlockContextObserved(...) == false` is insufficient:
adapter dictionaries are observations, not proof of Core inactivity. Preparation
fails closed for a missing bridge, unknown attachment, unverified session or
ambiguous owner. Existing transfer activation can still use its already-issued
Coordinator publication through the existing owner/source-current guards; this
is not permission to prepare without an attachment or activate without a source.
Existing ordinary negotiation remains ordinary; do not reinterpret its pending
messages as preparation or adopt them merely because their listener is ready.

The pure decision belongs in a small `FileTransferCoordinator` partial with a
separate session-preparation state/DTO. Do not construct a fake transfer state,
empty transfer ID, route publication, runtime-owner token or recovery contract.
`SessionFileTransferService` owns that state and applies lifecycle changes under
its existing gate. The adapter executes only the resulting bounded commands.

## 2. Scope and lifecycle

A preparation permit binds all of the following:

- The issuing service instance and current exclusive transport attachment.
- The verified session ID, both authenticated peer identities and an opaque
  local security-session incarnation. Reusing the same session-ID string is not
  a new security incarnation. Bind incarnation changes to actual security
  session establishment/replacement, not arbitrary status notifications.
- A monotonic preparation generation, the current file-admission generation,
  the selected payer and exact handshake episode/nonce once allocated.
- Its phase deadline and the applicable approval expiry. The 2026-10-03 approved
  lifetime split puts payer listener startup in `Starting`, bounded by the
  existing 120-second listener readiness limit. Only a Core decision on a fresh,
  exact current execution/lease observation enters `Pending` and issues the
  existing 60-second wire-handshake deadline. Duplicate requests renew neither
  phase; both remain approval-bound. Startup alone supplies no readiness or
  route authority. Completed evidence keeps the existing initial-readiness
  lifetime. GUI acceptance deadlines, provider requirements and route proofs
  are unchanged.

The permit contains no file route or runtime authority. A transport cannot
issue it to itself. The remote peer independently obtains its own Core permit;
an authenticated preparation offer is still only a request.

Use one current preparation episode per attachment/session. Exact duplicate
requests may coalesce without renewing deadlines or replacing a newer episode.
The service must observe verified, file-capable session approval through the
attached transport's security lifecycle; a snapshot with matching strings alone
cannot resurrect a revoked registration. Attaching to unknown prior ownership
must not infer an unused first-transfer slot.

Revoke pending preparation and invalidate unclaimed evidence on session/key or
peer replacement, approval loss/expiry, disconnect/rejection, detach, reset,
disposal, explicit acceleration stop, lease loss, or the first valid transfer
admission attempt. Admission covers both directions and begins before context
creation or asynchronous offer handling can expose a new owner. Invalid input
rejected before admission does not get to consume readiness.

The admission fence is sticky for that security incarnation, including failed
or canceled admissions. Resetting contexts or reattaching the same session does
not clear it. If lifecycle provenance is lost, deny preparation; do not guess
that the session is new. A genuinely new verified incarnation may start with a
new fence and permit. Prior-generation callbacks remain invalid.

## 3. Wire isolation and compatibility

Use three distinct preparation message types, with matching authenticated
secure-metadata message types: preparation offer, answer, and answer ACK.
Allocate new wire enum values without renumbering existing values. Reuse the
existing payload field shapes and secure-envelope machinery where appropriate;
the authenticated message type is the purpose discriminator. Keep exact session,
source, nonce, payer, lane, version and expiry validation on every leg.

An optional purpose field on an existing activation message is not sufficient:
an older peer can ignore it and execute the old activation handler. New
preparation messages must never dispatch into those handlers. Unsupported peers
may reject or ignore the new type; preparation then remains unavailable. Do not
silently retry the episode as ordinary negotiation or transfer activation.
Existing non-preparation traffic retains its existing wire behavior.

Carry the selected purpose and permit identity through queued sends, tasks,
pending replies, retransmissions, timeout callbacks and retained answer state.
Never recompute purpose from `Trigger`, current dictionary contents or the
presence/absence of a transfer after an await. An ordinary/activation answer or
ACK cannot complete a preparation episode even if nonce and session match.
This changes no file protocol version: regular-v4 and Tuna-v4 remain version 4,
post-Tuna fallback remains version 6, and active Tuna-v6 remains invalid.

## 4. Handshake evidence and prohibited effects

Local readiness is role-specific:

- Payer/listener: current usable file lane and exact accepted preparation answer
  received; the matching preparation ACK has been dispatched successfully.
- Dialer: current usable file lane and exact accepted preparation ACK received.
  Sending an answer or marking it pending is not local preparation completion.

ACK send completion does not prove remote receipt. The GUI gate requires a
fresh observation from each peer; do not add a fourth handshake message merely
to turn one peer's log into a supposed distributed readiness proof. Each peer's
local generation is independently allocated and must not be compared numerically
to the other peer's generation. Correlate the common authenticated episode.

The adapter may start/dial the exact preparation-owned listener/lane and exchange
the three control messages under a current permit. Core may retain the resulting
readiness evidence. Neither action may:

- Publish/commit a file route, mark a file transfer Tuna-active, or unlock/start
  a file runtime.
- Complete a runtime-unlock recovery contract or request a file activation
  handoff, hold, retry offer/dispatch, repair, replay or checkpoint.
- Change file progress, retire fallback workers, resume fallback, defer liveness
  or start bridge recovery.

In particular, do not reuse the ordinary answer/ACK continuation wholesale:
those paths call `CompleteRuntimeUnlockRecoveryContractIfActive` and
`RequestFileTransferTunaActivationHandoff`. Preparation must have an explicit
readiness-only continuation, including its error and timeout branches.
General transport availability is not a file route publication.

Stale/rejected callbacks produce diagnostics only. Resource cleanup must be
limited to the exact still-owned preparation generation; a late completion must
not stop a successor's listener/lane or a lane adopted by a real transfer.
Cancellation is not a substitute for checking scope when a callback runs.
Preparation must not take over an unrelated pending negotiation or shared lane.
No changes to screen-sharing negotiation or authority are included.

## 5. Transfer admission and concurrency

Admission must preserve already completed evidence while closing preparation to
new callbacks. It must not invalidate a good snapshot before the intended first
transfer can claim it, nor let a late answer create readiness after admission.

Required ordering at the service/Coordinator boundary:

1. Reserve the admission for the actual transfer ID and direction, binding the
   current attachment, session incarnation and exact completed preparation
   generation, if any. Close the preparation callback fence at this point.
2. Capture/claim readiness only for that reservation and exact generation. If
   preparation was incomplete, no later completion can supply this admission.
   Claiming binds one transfer ID and direction; retries for that exact claim
   are idempotent, but another transfer/direction cannot borrow it.
3. Recheck the reservation, current transport/lease evidence, security scope and
   deadlines before adopting it for the real Coordinator admission decision.
   Changed scope rejects the claim. Never replace it with whatever snapshot is
   newest, roll back another admission's claim, or synthesize missing evidence.

Use the existing `FileTransferInitialTunaReadinessEvidence` admission contract
and its freshness checks; retain the original source-capture timestamp. A
preparation permit or diagnostic view cannot substitute for that evidence.
`CanStartInitialFileTunaRoute` remains the final route gate. If evidence is
missing, preserve outbound normal-route selection and strict inbound rejection
of an unsupported initial-Tuna offer. The measured preactivated scenario still
fails if it starts regular; do not relabel it as an activation cycle.

Do not introduce synchronous Core callbacks while `accelerationGate` is held.
Do not add transport/lane operations under the service gate. Where a new boundary
requires both, use the existing prepare/execute/recapture pattern: reserve under
the service gate, perform adapter work outside it, then recheck/adopt under the
gate. Existing provider calls must not acquire a new reverse Core lock. Tests
must gate these interleavings explicitly rather than rely on sleeps.

## 6. Non-consuming paired observation

Add a separate read-only preparation observation API. It must not call
`IFileTransferInitialTunaReadinessProvider.TryGetInitialTunaReadinessEvidence`,
which consumes snapshots. Observing must not negotiate, claim a transfer ID,
refresh a deadline, issue authority, or reevaluate historical transfer evidence.
Read current preparation state and current transport facts, with scope recapture
if those reads cross locks. Return unavailable on an inconsistent read.

The observation includes request correlation, capture time, process ID and boot
identity, local role/peer identities, session/incarnation, requested direction,
preparation phase, common episode fingerprint, local preparation/snapshot/lease
generation, source time/expiry, consumed/revoked state and a reason code.
Report `authority_effect=none`; do not expose key material or an executable
permit. A pre-transfer snapshot may initially serve either direction, but an
observation names the queried direction and never binds it by observing. A real
claim fixes direction; a wrong-direction observation is then not ready.

For the GUI harness, use a smoke-only local request/response artifact bridge,
not a public network endpoint or a new user-facing workflow. Enable it only for
the existing GUI-smoke mode plus a harness-supplied, run-scoped directory. Use a
fixed per-process/boot subdirectory and fixed filenames; request contents cannot
select paths or commands. Accept only a bounded read-only observation request
(request ID, expected session, direction); write a complete response atomically.
Diagnostic I/O failure must not disrupt or alter runtime state. This bridge
must not read wallets, secrets or arbitrary files.

Replace the combined-log positive-line wait with a request to each known GUI
process. Require both responses to match the fresh request, process/boot,
session, peer pair and authenticated episode; sender queries outbound, receiver
queries inbound. Both must be unexpired and ready, with capture times after the
request. Preserve both responses as diagnostics. Do not compare local generation
numbers, accept an earlier request's file, or reuse a single peer's observation
twice. Keep the existing bounded wait; no longer sleeps or timeout tuning.

These observations are a precondition, not an atomic two-peer admission or
acceptance verdict. Actual admission revalidates independently; actual acceptance
still needs both Coordinator publications and the existing transfer proofs.
Use this gate only for the affected preactivated first-transfer scenarios.

## 7. Source touchpoints and unchanged guards

- `SessionFileTransferService.cs`: attachment/security lifecycle, outbound
  `TryStartSendAsync`, inbound `HandleIncomingOfferAsync`, reset/disposal;
  `SessionFileTransferService.OutboundTransferController.cs`: detach.
- New focused Core partial/bridge: session preparation decisions, lifecycle
  fence, evidence claim and observation; no changes to route laws.
- `NknSignalingTransport.Acceleration.cs`: request-mode selection, isolated
  preparation handshake, retained callback purpose and readiness capture.
- `MsgType.cs`, `NknEnvelopeRouter.cs`, secure-control dispatch/metadata mapping,
  and payload serialization: distinct preparation wire types and no downgrade.
- `SessionRuntime`: smoke-only observation carrier; `GuiSmoke-Windows.ps1`:
  `Wait-TunaGuiDiagnosticReadinessBeforeMeasuredTransfer` and its callers.

Keep `IsRuntimeUnlockInitialActivationAnswerBoundarySourceCurrent`, installed
retry receipt checks, retired answer barriers and existing late-callback guards
strict. Do not use preparation to exempt any existing transfer proof failure.
No queue/concurrency, chunk size, retry cadence, throughput, wallet, installer,
GUI invite or screen-sharing tuning is included.

## 8. Implementation and verification gates

1. **Core model and service fence.** Implement pure eligibility, phase changes,
   revocation, admission reservation/claim and non-consuming observation.
   Test missing attachment/security, both directions, duplicate requests,
   expiry, consumption, same-session reset/reattach, new incarnation, and
   transfer-start-before/after-completion interleavings. Do not wire new runtime
   negotiation until these contracts pass.
2. **Isolated adapter handshake.** Extend the real paired fake-network fixture
   for both payer roles. Test completed preparation, held answer and held ACK;
   queue/await races with transfer start, stop, detach, lease/session replacement;
   wrong peer/nonce/payer/type; duplicates and expired replies. Assert exact
   readiness and zero prohibited file effects before actual admission. Include
   unknown-type/legacy-peer rejection and no downgrade. Keep existing activation,
   installed-retry, source-publication and ordinary-negotiation regressions.
3. **Paired diagnostic gate.** Verify repeated observations do not change
   consumed IDs/direction, generations, deadlines, permits or publications.
   Test one peer ready, pending-answer-only, mismatched episode/session/process,
   PID reuse, stale response, malformed request, expiry and revocation. Test the
   PowerShell gate on both supported hosts.
4. **Targeted validation.** Run directly affected Core/Infra/analyzer lanes and
   at most one broader contract lane after stabilization. Rebuild the portable;
   run strict preflights and affected initial-Tuna GUI scenarios outside the
   restricted sandbox. Stop at a new first proof edge and retain its artifacts.
   No full matrix repeat or live-256 rerun is justified by this contract alone.

### Core-model implementation checkpoint

`FileTransferCoordinator.SessionPreparation.cs` implements a separate immutable
session state, scoped permits, exact role-specific completion, revocation and
retirement, first-admission reservation, single-transfer readiness claim, and a
non-consuming observation. It retains the original source timestamp and uses
the existing initial-Tuna evidence type only after a real transfer reservation.
The existing `CanStartInitialFileTunaRoute` gate remains unchanged.

Seventy-five distinct new Core cases pass across focused runs, plus eight
existing initial-readiness regressions. See
`artifacts/session-preparation-core-20260921/RESULTS.md`. These are pure model
tests, not real service lifecycle, wire compatibility or paired runtime tests.
The queued-work permit check is a pure current-state decision, not an execution
lock; the service bridge must serialize its use with lifecycle/admission changes.

At this checkpoint gate 1 was incomplete: the model had no production callers.
The service integration below now supplies the required provenance and hooks.

### Service-lifecycle/admission implementation checkpoint

Gate 1 is now implemented. The separate lifetime bridge is installed at actual
NKN control-key installation and reset; unbound key installation invalidates
the old lifetime without opening a new one. Verified approval binds the epoch's
session and peers. Diagnostic security IDs/fingerprints remain diagnostic-only
and are not inputs to this bridge. No new preparation wire handler is enabled.

One service owns each preparation attachment. An attachment records the current
installation generation as its baseline: attaching after key installation, or
reattaching the same session, cannot claim unknown earlier admission history.
The normal app host/helper paths attach the file service before starting their
connection/approval flow. A later verified key installation can create a new
eligible incarnation; repeated security notifications cannot.

Service callbacks validate the exact attachment and epoch, then make/adopt Core
decisions under the service gate and the epoch's retirement fence. That fence
allows Core-state work only, never transport/lane operations. Reset, detach,
disposal, rejection and disconnect revoke service eligibility; key retirement
or approval/peer loss immediately makes the bound epoch unusable. Unknown or
late approval after a transfer attempt cannot reopen the first-admission slot.

Outbound admission and incoming-offer receipt reserve the first-transfer slot;
incoming reservation precedes queued service handling. Completed preparation
evidence is retained for that exact transfer/direction. Lease reads happen
outside the service gate, followed by exact reservation/lifetime recapture and
the unchanged real Coordinator initial-route admission. Missing prepared lease
evidence cannot borrow a legacy positive snapshot. Non-preparation paths retain
their existing evidence provider behavior.

The service observation API reads the lease outside the gate and rejects a
changed state/lifetime on recapture, without registration, consumption or
authority issuance. The smoke artifact carrier and paired GUI gate are still
pending, as are adapter stop/lease-loss handling for the new negotiation purpose.

Verification: `artifacts/session-preparation-service-20260921/RESULTS.md`:
26 distinct new integration cases and 123 focused regressions pass. Tests use
real services/Coordinators with synthetic preparation proofs, plus real NKN
approval/key lifetime over the fake network. They do not validate a new wire
handshake or change the failed full-matrix acceptance verdict.

Next: gate 2, the isolated authenticated preparation offer/answer/ACK path and
its exact-purpose continuation, cancellation and late-callback tests. Preserve
the active-transfer source-publication checks and legacy wire behavior.

No historical evaluator, portable build, live run, commit or push was performed.

### Adapter wire-boundary checkpoint (gate 2 remains incomplete)

Preparation has distinct wire values 52/53/54 and authenticated metadata names
`session_preparation_offer`, `session_preparation_answer`, and
`session_preparation_answer_ack`. Existing values are unchanged. The production
router never forwards these types to ordinary/activation handlers; it currently
authenticates/validates and rejects them with `execution_not_enabled`. No new
outbound request selection, handshake worker, readiness completion or lane
operation is enabled by this checkpoint.

The reader freezes purpose, exact local attachment/security epoch, nonce, payer
decision, deadline and role/address fields into immutable input. It requires
the approved peer/session, file-only lanes, bounded lifetime and exact secure
request ID. The same security gate used by key installation protects decrypt
and replay tracking, followed by lifetime recapture. Authenticated input is not
permission. A reply-current check additionally requires the exact current Core
permit, role-specific answer/ACK type and episode/deadline; it is explicitly
not an execution fence. Preparation decryption does not publish session liveness
proof, while legacy control decryption retains its behavior.

At the wire-boundary checkpoint, the blocking prerequisite was visible in
`INknTunaAccelerationSession` and `NknTunaAccelerationLane`: listener adoption
has an exact snapshot, but dialer start may reuse any current dialer and the
only stop API stops the current shared lane/supervisor. A preparation timeout
or stale continuation must not use that API to stop a successor/adopted lane.
An adapter generation number checked before `StopAsync` would still race.

The physical-boundary work planned at that checkpoint was:

1. Add an exact preparation execution/lease identity at the physical lane
   boundary, fenced against admission/cancellation and key/owner replacement.
   Starting preparation must not reuse/take over unrelated pending/current work.
2. Add exact-owner cleanup for listener **and** dialer resources. A rejected
   asynchronous completion must dispose only resources it created. Transfer
   adoption or successor replacement must make old cleanup a no-op.
3. Test blocked-start, replacement, cancellation and adoption races with fake
   sidecars; then wire the isolated offer/answer/ACK continuations and existing
   Core lease-observation bridge. Preserve ordinary, activation and retry paths.

See `artifacts/session-preparation-wire-20260921/RESULTS.md` for wire tests and
affected regressions. These tests do not prove successful paired preparation,
held-reply execution races, a legacy binary handshake, or live acceptance.

### Physical lane ownership checkpoint (gate 2 remains incomplete)

The physical prerequisite now has an idle-only preparation start API, exact
lane/start/client/process receipts, and conditional cleanup for both listener
and dialer resources. Start generations fence publication after stop, disposal,
cancellation or replacement. Preparation neither coalesces with pending work
nor reuses an unrelated current client. Generic startup cannot take over an
unretained preparation client. Unsupported implementations fail closed.

Listener startup returns the immutable process/ownership receipt captured at
ready publication; it does not read a fresh snapshot afterward and accidentally
borrow a same-address successor. Listener adoption rotates ownership generation,
and lane retention rotates client generation. Adoption and cleanup also reject
an explicitly supplied replacement snapshot that does not match the client's
stored physical listener receipt. Exact cleanup detaches under the
ownership locks and disposes/stops only the captured resources outside them.
The runtime wrapper forwards these APIs without replacing another child or
listener configuration for a preparation request. Ordinary payer-switch stop
and wallet configuration behavior are preserved.

Preparation cancellation is linked to connection startup only. Once connected,
an exact-lease callback owns revocation; retention retires that callback without
leaving the client's reader/writer lifetime linked to the old preparation token.
Loss of an unretained prepared client disposes that client and detaches its exact
listener, without stopping a replacement installed during cleanup.

These are physical resource capabilities, **not Core authorization**. The new
start API still has no production preparation caller. The adapter must bind its
cancellation to the exact Core permit, attachment/security lifetime, deadline
and admission retirement before enabling execution. It must obtain the Core
admission/ownership decision before retaining a lane. Neither execution identity
strings, process receipts, availability nor retention grants file-route/runtime
authority. No transport work belongs inside the Core epoch state fence.

Verification: `artifacts/session-preparation-lane-20260921/RESULTS.md` records
42 new ownership cases and 19 existing regressions passing. Both listener and
dialer publication/cleanup seams are exercised; actual listener IPC uses loopback
TCP. Process ownership tests use unstarted process identities, not paid Tuna
processes. A legacy fake-executable test stalled in the sandbox and passed in
isolation outside it; the aborted runs are recorded, not counted as passing runs.

Next bounded work is the isolated adapter offer/answer/ACK executor: bind Core
permit retirement to these physical resources, reject stale queued continuations,
isolate preparation callbacks from active-transfer effects, connect exact lease
observation/completion to the service, and test both payer roles with held replies
and first-admission/security races. The paired diagnostic gate follows that.
The production wire handler still returns `execution_not_enabled`. No new
portable build, live test, commit or push was performed at this checkpoint.

### Core-bound physical execution checkpoint (gate 2 remains incomplete)

The service now binds an exact Coordinator permit to a revocable execution
lifetime before physical startup. A point-in-time `IsCurrent` check alone was
insufficient: admission or key retirement could win after that check and before
the queued lane call. The new lifetime is registered with the exact attachment
and actual security epoch. Retirement changes its cancellation state immediately;
registered cleanup runs asynchronously, never inline on a Core gate owner.

Pending outbound or inbound admission, reset, detach, disconnect, disposal,
approval/peer loss, key replacement and episode retirement cancel pending work.
Completion and deadline retirement serialize through a Core-state-only fence.
Completed readiness keeps the existing five-minute/approval-capped lifetime;
an old pending timer cannot cut it short. First admission closes preparation
callbacks but preserves completed resources for that exact Core reservation.
This preservation does not itself transfer physical ownership to a file runtime.

The adapter's bounded physical executor carries the binding through queued and
awaited startup, coalesces only the same execution and remote address, uses the
Core cancellation token directly, and recaptures both Core and physical ownership
after startup. A late result is cleaned only by its exact receipt. Explicit
acceleration stop and transport disposal also retire the binding. The dialer
entry requires the separately authenticated, exact-episode preparation offer.
No legacy startup or shared-lane stop is used by rejected-work cleanup.

A revoked execution cannot produce readiness observation or admission evidence,
even when a lease provider returns positive facts. Loss of preparation evidence
does not invalidate the otherwise-current transfer reservation: outbound normal
route selection remains available, and initial-Tuna evidence remains strict.

Verification: `artifacts/session-preparation-execution-20260921/RESULTS.md`:
56 new cases and 28 affected regressions pass. Tests use real services and NKN
security approval/decryption over `FakeNknClient`, with gated fake physical lane
results. They cover both payer roles, queued/busy startup races, exact duplicates,
late physical replacement, expiry and exact admission preservation. Completion
proofs in service tests remain synthetic; this is not a paired handshake test.

The executor currently has no production request/wire call site. Before enabling
it, wire isolated offer/answer/ACK sends and role-specific continuations, isolate
preparation lane events from ordinary activation/recovery handlers, implement
exact lease observation and admission-time physical ownership handoff, and test
held answer/ACK and first-admission races. Only then add the paired GUI diagnostic
gate and proceed to a fresh portable/targeted live validation. The wire handler
still returns `execution_not_enabled`; no live acceptance verdict has changed.

### Isolated handshake/event checkpoint (gate 2 remains incomplete)

The three distinct preparation wire types now dispatch to a separate executor.
The dialer independently requests its exact Core permit from the attached
service before physical startup. Neither an authenticated offer nor a ready
listener is itself authority. The payer entry still requires a Core-issued
permit and is not selected by the public runtime-unlock request yet.

Each leg uses the exact execution lifetime and the existing high-priority
control queue, without changing queue policy. Queue completion means the NKN
send completed, not merely that it was enqueued. The payer completes readiness
only after its exact accepted answer and successful ACK dispatch; the dialer
completes only after exact ACK receipt. Completion recaptures the same physical
lease and Core episode. Repeated local requests coalesce without a new listener,
new wire episode or refreshed readiness time. Send failure, retirement and
stale physical results cannot complete readiness or trigger an ordinary retry.

Preparation lane events now carry their captured execution identity. Client
ready/loss and listener terminal/rejection/error/cap events cannot fall through
to ordinary activation, fallback or recovery callbacks. Listener cap events
retire preparation rather than requesting an active-runtime handoff. The
identity is captured before any callback can replace ownership; old events
remain preparation events even after a successor is installed. Preparation
control sends also do not increment existing fallback-control evidence.

Verification: `artifacts/session-preparation-handshake-20260921/RESULTS.md`:
194 distinct latest case results pass (43 new, 151 existing). Tests include the
real encrypted three-leg exchange over paired `FakeNknClient` transports and
real services for both payer roles, held answer/ACK, lost ACK, failed ACK send,
queued-send cancellation, pending admission, stop/reset/detach/key replacement,
physical successor cleanup, and listener event-purpose capture. Real listener
loopback IPC verifies ready/loss event tagging. Four legacy log assertions failed
with sandbox `access_denied` and passed unchanged outside the sandbox; their
failed results remain in the artifact record.

The remaining gate-2 boundary is exact lease observation and Core-checked
admission-time physical ownership handoff. The prepared resource must stop being
owned by its preparation timer/cancellation before an actual file runtime adopts
it, without allowing stale cleanup to touch a successor. Until that boundary is
implemented, the adapter does not provide preparation admission evidence or
select preparation for `RequestAccelerationNegotiationAsync("runtime_unlock")`.
Existing request-mode precedence and transfer-scoped source guards are unchanged.
Next come the paired diagnostic bridge and targeted portable/live validation;
the full-matrix and live-256 proof gaps remain unresolved. No live run, portable
build, commit or push was performed at this checkpoint.

### Admission ownership checkpoint (public selection remains pending)

The adapter now supplies exact current preparation lease observations to the
reserved admission. Core claims the original readiness evidence, then issues a
separate admission resource lease only for the exact reservation and evidence
objects. Copies, other transfer IDs and other directions cannot borrow it.
Physical retention rotates the cleanup receipt outside the service/epoch gates;
it does not publish a route or mark the transport active.

After the real transfer leg is admitted, Core issues its actual route-publication
token. The service adopts resource ownership only against that current token,
matching session, transfer, direction, `file_tuna_v4` and protocol 4. Physical
activation runs outside Core locks. Route-selected/hint publication then uses
that same token, with one authority effect rather than a second publication.
An activation failure terminates the admitted transfer before sending its offer
or exposing a successful inbound decision. Missing retention preserves ordinary
outbound selection; unsupported inbound Tuna admission remains strict.

Adoption retires the old preparation execution without stopping the rotated
physical lease. The new owner follows actual transfer cancellation, attachment,
security epoch, explicit stop and approval expiry. Its pending readiness deadline
does not become a new transfer timeout. Retention keeps events tagged with the
provisional admission identity; activation releases that purpose isolation only
for the adopted owner. Late preparation/provisional events remain diagnostic.
The real listener supervisor permits only the exact preparation-to-retained or
preparation-to-admitted transition, preserving process/run/generation/peer/lane
checks. Cleanup still never invokes shared-lane stop.

Verification: `artifacts/session-preparation-admission-20260921/RESULTS.md`:
175 distinct latest case results pass (37 new, 138 existing). Both payer roles
and transfer directions use real services and encrypted preparation/control
traffic over paired `FakeNknClient` transports. Tests cover publication ordering,
copied bindings, retention/activation races, expiry and exact successor cleanup.
Real loopback IPC proves that old preparation cancellation cannot stop the
retained connection; real-supervisor tests verify ownership guards without
starting a wallet or paid sidecar. The initial run exposed an implementation
ordering bug (adoption before publication), which is fixed and retained in the
results record. The final Release build has 0 errors and 255 warnings.

Next is public `runtime_unlock` purpose selection with existing fallback/retry/
transition precedence, followed by the non-consuming paired GUI diagnostic gate.
Only then rebuild the portable and run targeted initial-Tuna GUI validation with
strict preflights outside the sandbox. No public selection, live test, portable
build, commit or push was performed here; original live proof gaps are unchanged.

### Public runtime-unlock selection checkpoint

`RequestAccelerationNegotiationAsync("runtime_unlock")` now asks Core to select
preparation versus the existing transfer-activation path for a file-capable
session. The service checks its current attachment/security lifetime and real
transfer contexts under its gate. Eligible/pending/ready preparation preserves
the exact episode; a current nonterminal Coordinator publication selects the
existing transfer path. Missing publication, ambiguous ownership, revoked scope,
expired evidence or a closed first-admission fence cannot select preparation.
A late attachment cannot prepare, but an actual subsequently published transfer
can still activate. Existing transfer integrations without the new attachment
retain their already-Coordinator-published owner path and its source guards;
mere data-session or route-cache entries do not qualify.

Existing post-fallback/installed-retry and route-transition precedence stays
ahead of preparation. The new request does not reinterpret ordinary pending
offers/ACKs or queue a source-less initial activation when preparation is denied
or busy. Outgoing and incoming preparation use a distinct scheduling-slot owner
with exact release; no Core callback runs under `accelerationGate`. Ordinary
non-runtime-unlock requests and sessions without file-transfer capability keep
their existing path. Preparation still negotiates only its bounded file lane.

The existing helper/helpee preference wait is reused without timeout tuning or
ordinary payer-intent sends. Its request captures attachment and epoch before
waiting, then rechecks the same binding and Core decision. A request delayed
across a new key cannot prepare the successor session. Incoming helpee
preparation can win while the helper waits; the helper then observes that exact
dialer episode instead of starting another payer. A non-listening peer waits
for an authenticated preparation offer rather than invoking ordinary activation.

Verification: `artifacts/session-preparation-request-20260921/RESULTS.md` records
202 current passing cases (30 new, 172 existing). Public requests for both payer
roles complete encrypted fake-network preparation, remain route-inactive before
admission, then produce both actual Tuna-v4 admissions. Cases also cover duplicate
requests, ordinary busy slots, missing/late attachment, unowned data sessions,
held answers, cancellation/admission races, helper preference and stale waits.
The initial-activation answer/source guards remain strict in four dedicated
legacy-executor regressions. One old installed-retry preflight fixture had
obsolete reflection calls already present at HEAD; it now uses a real exact
Coordinator retry installation and retains its original preservation assertions.
Only those four log-dependent cases ran outside the sandbox; no live network,
wallet, paid sidecar or GUI was involved. Final build: 0 errors, 247 warnings.

Next: the smoke-only non-consuming per-process observation bridge and paired GUI
readiness gate. Then rebuild the portable and run targeted initial-Tuna scenarios
with strict external-sandbox preflights. No full matrix, live-256 rerun, portable
build, commit or push occurred in this checkpoint; original live gaps stay open.

### Paired GUI diagnostic checkpoint

The service now exposes a non-consuming diagnostic projection with exact
state/attachment/epoch recapture around the existing live lease observer. Scope
discovery never registers an eligible session. Requests cannot claim readiness,
renew its original source/expiry, start negotiation, or grant route authority.
The projection exposes a SHA-256 fingerprint of shared authenticated episode
fields, not the nonce, permit, key material or independently allocated peer
generations. An inconsistent read is unavailable; first admission closes both
diagnostic directions.

`SessionRuntime` starts/disposes a local artifact bridge only when GUI-smoke mode,
a harness-supplied absolute run directory and per-launch boot GUID are present.
It uses `<pid>-<boot>/identity.json`, `request.json`, and `response.json` with
atomic complete writes. Requests are bounded to 4096 bytes and fixed schema;
unknown fields, path/command selectors, invalid directions and future timestamps
are rejected. I/O failures are diagnostic-only. Repeated polling of one request
does not regenerate its response or change service state.

The harness pins discovery to its two known process objects, boot GUIDs, process
start times, roles, session and local incarnations. It requests outbound sender
and inbound receiver observations separately and requires fresh, unexpired,
unconsumed responses with matching peer identities and episode fingerprint.
Local generations need only be positive, not equal between peers. Read responses
and the accepted pair are retained in the scenario artifacts. These helpers live
in the already-hashed `GuiSmoke-Windows.ps1`, preserving the existing acceptance
harness provenance file set.

Only the preactivated first-transfer wait changed. The later second-transfer
reactivation check, actual Coordinator publication checks, strict route-cycle
proof and the existing 150-second bounded wait remain unchanged. Diagnostic
readiness is not atomic admission or a live acceptance verdict.

Verification: `artifacts/session-preparation-diagnostics-20260921/RESULTS.md`:
171 passing cases (35 new, 136 existing), including both PowerShell hosts,
malformed/stale/single-peer/mismatched proof rejection, reset during lease read,
bridge enablement/disposal, and both payer roles and send directions using
encrypted fake-network preparation followed by actual Coordinator admissions.
Release build: 0 errors, 247 warnings. No live network, GUI, portable rebuild,
wallet/sidecar run, commit or push was performed.

Next: rebuild the portable, run strict preflights outside the restricted sandbox,
then targeted initial-Tuna GUI validation. Do not run the full matrix yet. The
separate live-256 exact-resend and regular inbound-terminal gaps remain open.

### Targeted live checkpoint

The fresh local-only portable and strict global/scenario preflights passed.
`active-tuna-v4-64mb` then passed the paired readiness gate for a helpee payer
sending to the helper, but failed after zero payload progress. The original GUI
classifier selected an inbound initial route-publication snapshot boundary;
source inspection exposes a regular-only initial-owner association to reproduce.
The manifest-to-first-V4-credit path is a separate unresolved runtime edge.
No further scenario or production edit was made. The intentional dirty source
also prevents clean acceptance provenance. See
`artifacts/session-preparation-live-20260921/RESULTS.md` and the bounded RCA above;
do not reinterpret this as a passing route-consistency or live acceptance result.

### Initial-publication analyzer and feedback boundary checkpoint

The analyzer now recognizes a complete, exact Core-issued initial Tuna owner and
its first epoch-zero publication, without relaxing Regular republication or later
Tuna transition requirements. Six focused analyzer cases cover both directions,
independent generations and second-publication rejection, with 28 invalid-evidence
controls per initial-route case. Existing Regular evidence coverage is preserved.

The paired-service diagnostic advances beyond offer admission to accept and
manifest receipt. All four payer/direction cases reproduce the commit-only
bootstrap feedback registration rejection. These are diagnostic passes, not
successful credit/payload tests. Runtime source guards remain unchanged; the
next work is the exact Core-owned initial-publication source binding described
in the live RCA addendum. No new live run, portable rebuild or commit was made.

### Initial-publication source-binding checkpoint

Core now projects a current Tuna source scope for either the exact initial
publication or a current transition commit/publication pair. It does not invent
a commit or generalize epoch-zero validity. Feedback, writes and outbound source
callbacks use the shared projection while retaining exact session/provider,
operation, pause/hold and physical-finalization fences. Initial-source binding
is not admission to the separate fallback exchange; that commit-only admission
and its adapter observation capture remain unchanged.

The previous four feedback-boundary diagnostics now prove credit receipt and
four payload bytes in the joined real-service/fake-physical-lane tests. Two
reset-before-submission cases reject late feedback. Eleven Core cases and two
callback-evidence cases add ownership/lifetime and analyzer coverage. Together
with all 125 directly affected bootstrap regressions, 144 distinct cases passed.
The results preserve an aborted combined regression run and its successful
bounded joined-lane isolation; no selected test is missing from the coverage.

Next: accumulated-change review, clean checkpoint, fresh portable and the single
strict `active-tuna-v4-64mb` rerun. No full matrix or live-256 yet. No live run,
portable rebuild, commit or push occurred during this implementation checkpoint.

### Accumulated pre-commit review checkpoint

The accumulated Core/service, authenticated preparation, physical ownership,
admission, GUI diagnostic and initial-source/analyzer changes were reviewed,
including untracked production files. No new actionable correctness finding was
identified. See `file-transfer-precommit-review-20260921.md` for scope, checked
test records and remaining limits. No runtime edits, test reruns, historical
authority reevaluation or live run were made during review.

Next is the clean local checkpoint, then a fresh portable and only the strict
`active-tuna-v4-64mb` validation. This is not approval of live correctness or the
full matrix; all previously documented live gaps remain open.
