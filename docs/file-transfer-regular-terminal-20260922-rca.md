# Regular-NKN inbound terminal gap: RCA and bounded fix plan

Date: 2026-09-22. Current HEAD:
`012101e40a7d0d650027edbba67e8c0314a330fa`.
Status: bounded constructor fix implemented and verified offline; targeted GUI
validation remains pending.

## Implementation follow-up

The parameterless constructor now assigns `regularV4PressureAdapter` to the
exact `RealNknClientAdapter` instance it already owns. This is one production
line; no second backend, constructor refactor, authority rule, pressure policy,
terminal guard, snapshot writer or acceptance predicate changed.

Fifteen distinct focused tests pass:

- The constructor regression now observes the same real client/backend and
  receives `current_transfer_not_active`, preserving the real backend's guard.
- Two new Service controls exercise a pending installation followed by terminal
  retirement. A successful retry settles the exact authority and permits the
  Coordinator terminal transition and Service terminal snapshot. A rejected
  retry preserves the pending binding, owner, terminal generation, nonterminal
  state and data session, with no settlement.
- The complete affected backend lane and three existing pending/terminal
  controls pass, including real receipt ownership, inactive/unsupported guards,
  lifecycle tracking and rejection of late receipts after retirement.

Receipts: `artifacts/regular-terminal-fix-20260922/`. The standalone constructor
case overlaps the affected lane; counts are deduplicated, not added blindly.
No final failures, skips or aborts. The new Service controls use the existing
loopback bridge and a requested Failed terminal state; they test the shared
retirement gate, not file/hash completion, the app log writer or live networking.

The original live failure remains unchanged. Next: review/checkpoint this small
change set, rebuild the portable, and validate only `regular-nkn-v4-64mb` with
strict preflights. No live run, package build, commit or push occurred during
the implementation step. The original diagnosis follows.

## Finding

At the diagnosed checkpoint, the parameterless `NknSignalingTransport`
constructor created a `RealNknClientAdapter` but never assigned it to
`regularV4PressureAdapter`. The injected constructor did assign that same
production client. Consequently, the plain/default NKN transport reported its
pressure-install backend as unsupported even though it owned a real adapter.

The first missing edge is delivery of a valid pressure-install receipt through
the actual backend, not the later terminal log or snapshot writer. A pending
repair-pressure binding cannot be retired because the required installation
retry receives another unsupported rejection. The service correctly refuses
to terminalize while that binding remains unresolved. Do not bypass this guard.

This is a confirmed current constructor defect and a source-backed explanation
of the original regular scenario. The fresh offline reproduction is not a new
GUI transfer or proof that the proposed fix closes every acceptance gate.

## Original evidence and custody

Latest relevant regular capture found:
`artifacts/phase5-full-gui-matrix-20260921/live/20260921T051528Z/regular-nkn-v4-64mb/`.
Its source checkpoint is `bdfd45b964956867b02f3f00ae8b12981a8a270a`.
No newer retained regular-scenario result was found; fake route-acceptance
fixtures were excluded from the live-artifact inventory.

The retained input SHA-256 still matches its original attempt manifest:
`6047ff3c6ff09c21703b5d6366dc96fe7ec04dbb35f17a84ba481c0893354b9c`.
Raw operational log SHA-256:
`ded28de9f2628e2c67d1e15ae22668d0902c4edc70ac07f2ce65f7852deb61d9`.
Original full/compact parity passes with 17 boundaries, 16 transitions and
four effects; the original stream verdict remains incomplete for inbound
Coordinator `fte1-f748cb7b8fb52c45c0e72827`. No historical analyzer or authority
evaluation was rerun and no original verdict was reclassified.

Relevant retained line numbers (the selected raw-log rows have the same numbers):

| Lines | Evidence | Conclusion |
| --- | --- | --- |
| 1173, 1180, 1509, 1515 | NKN.Tuna pressure-install rejection for the same authority, generation 1 | Failure exists before terminalization |
| 12284-12289 | Finalization starts, sparse hash completes, integrity passes and destination finalization succeeds | File/hash/final-path work reached completion |
| 12290-12292 | Lifecycle completion sent, including the control path | Completion notification was sent before local terminal admission |
| 12293-12297 | Pending generation-1 binding retried at `coordinator_pending_install_retry`; installation rejected; binding invalidation rejected at `inbound_transfer_terminal` | Pressure retirement is the missing pre-terminal edge |
| 12298 | `filetransfer_terminal_transition_rejected`, receiver writer sequence 4713 | Explicit runtime rejection, not merely an absent terminal row |
| 12319, 12324 | Outbound terminal and outbound stream closure | Sender completion does not establish receiver terminalization |

Receiver writer `p27736r5fde58ce69c34836` continues recording after the rejection,
through sequence 5795 at 05:19:22 UTC. The GUI times out at 05:19:26 UTC. This is
not evidence that the writer stopped at the missing terminal boundary.

The captured `decision_reason` values are redacted. Do not quote an unredacted
reason as if it appeared in that log. The unsupported-backend interpretation
comes from the matching NKN.Tuna emitter branch in source, supported by the
fresh constructor reproduction below. It does not come from reconstructing
authority from log absence or observation timestamps.

## Code trace and freshness

- `NknSignalingTransport.cs:360`: the default constructor creates `client` but
  leaves the readonly pressure-adapter field null. The injected constructor at
  line 458 wires it to the real client.
- `NknSignalingTransport.Acceleration.cs:2312`: a null pressure adapter returns
  `regular_v4_frontier_repair_pressure_install_unsupported` before the real
  backend can evaluate the coordinator-issued command.
- `SessionFileTransferService.PullTransferSessionV4.cs:9495`: pending inbound
  pressure retirement first retries the exact installation. Rejection leaves
  the pending binding intact and returns false.
- `SessionFileTransferService.OutboundTransferController.cs:464`: the terminal
  path checks retirement before submitting `Terminalized` to the Coordinator,
  setting terminal state, raising the app snapshot or disposing resources.
  Its early return explains the missing downstream events.
- `SessionRuntime.cs:1506`: the app terminal projection requires an actual
  terminal snapshot. `SessionFileTransferService.cs:26706` requests snapshot
  stream completion from inbound resource disposal; the rejected terminal path
  has not reached that disposal.

The surrounding application has changed since the matrix. A bounded source
comparison, normalized to LF, nevertheless found all six directly relevant
slices unchanged: default constructor, injected constructors, pressure bridge,
pending-retirement helpers, inbound terminal path and inbound finalizer.
The complete real-adapter, snapshot-emitter and operational-writer files are
also unchanged. Thus this specific explanation is not borrowed from a changed
Tuna/admission path. The reproduction builds and runs current sources.
Any expansion into changed startup/admission/route code must reassess freshness
and obtain a fresh targeted run before a runtime correction there.

## Preimplementation deterministic reproduction

Added `NknPressureBackend_ParameterlessConstructorUsesItsRealClient` to
`tests/nLink.SmokeTests.Core/NknRegularV4PressureBackendTests.cs`.

It uses the actual parameterless constructor, with an isolated test identity
under the workspace artifacts directory and Tuna disabled. It makes no
Connect/Host call and starts no live NKN run. A command staged by the existing
Coordinator test helper is submitted to the actual pressure bridge. With no
active transfer, a correctly wired real backend must reject it as
`current_transfer_not_active`; it must not report a missing backend.

Observed result in the newly built Debug harness:

```text
ClientType=RealNknClientAdapter
BackendType=null
SameClient=false
InstallStatus=Rejected
Reason=regular_v4_frontier_repair_pressure_install_unsupported
```

Receipt: `artifacts/regular-terminal-rca-20260922/constructor-and-terminal-red.trx`.
One new regression fails on the expected-versus-actual reason above; four
existing controls pass: injected production-client ownership, unsupported and
inactive backend rejection, and terminal-retirement rejection preserving the
nonterminal service scope. No skips or aborts. This is an intentional red
regression, not a passing implementation result.

Observation JSON:
`artifacts/regular-terminal-rca-20260922/constructor/6a2f7f99201744e9911c9d9348df6ff9/observation.json`.
A Windows-only guard was subsequently added to the test because it constructs
the protected identity store; this does not change the exercised Windows path.
No aggregate lane was run. Existing build warnings remain outside this slice.

The previous production-client test exercises the injected constructor, so it
does not detect the missing assignment in the independent default constructor.
The existing terminal guard test covers rejection of an installed binding;
it must remain strict, not be rewritten to accept unsupported retirement.

## Smallest fix and verification plan

1. Wire `regularV4PressureAdapter` to the exact real client immediately after
   creating that client in the parameterless constructor. Reuse the owned
   instance; do not create a second backend or change authority/receipt rules.
   Avoid a constructor refactor or changes to pressure policy and scheduling.
2. Make the new constructor regression pass. Keep the inactive/unsupported
   negative controls, injected-client ownership, and terminal-retirement guard
   passing. Verify that installed/pending/predecessor ownership still follows
   real backend receipts and late receipts cannot restore retired authority.
3. Run the directly affected `RegularV4PressureBackend` lane once after the fix
   stabilizes, plus only the relevant pending-install/terminal service tests.
   Add a focused service control if those tests do not cover successful pending
   install retry followed by exact terminal retirement. No generic aggregate
   suite is needed for the constructor assignment.
4. Rebuild the portable package, then run only `regular-nkn-v4-64mb` outside the
   restricted sandbox with strict preflights. Require inbound and outbound
   Coordinator terminal proof, both stream closures, app terminal projection,
   integrity and finalized evidence. Preserve and report any new first edge;
   do not infer success from file size, sender completion or SHA alone.

No live-256 or full-matrix rerun is needed to validate this separate defect.
Do not change terminal guards, synthesize stream closure, clear pending state
from a rejected callback, tune throughput, or alter the missing-Tuna predicate.
Failed-attempt finalization and parent evidence issues remain separate.

## Handoff

The constructor wiring fix and focused offline verification are complete.
The working tree holds the one-line production correction, constructor
regression, pending-terminal Service controls and documentation. Review and
checkpoint those changes before the fresh portable/targeted regular-NKN GUI
gate. No package, live run, commit or push has occurred for this fix.
