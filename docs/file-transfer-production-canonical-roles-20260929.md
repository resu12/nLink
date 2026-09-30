# Production canonical app-role binding

2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus preserved worktree changes.

## Scope

This step connects the consumed canonical attempt to the original helpee/helper
requests, app processes and report objects in the existing optional scenario
branch. The actual live scenario entry remains Context-only: it passes neither
`FrontierOwner` nor `CanonicalAttempt`. Protected source/destination handoff
and downstream pair adoption are still gates, not completed by role binding.

No acceptance rule, resource budget, route authority, retry behavior or
performance setting changes. No live NKN, GUI automation, full matrix, portable
rebuild, commit or push is part of this step.

## Implementation

`LocalGuiWriterCanonicalRoles.cs` moves the established role implementation out
of the test profile into the normal loader. The core checks are unchanged:
only a current, consumed, original attempt can create one request per exact
role; launch requires the same registered request; matching requires the original
request, process, report and native attachment with its exact PID/creation time.
Unknown/swapped roles, copies, repeated launches and stale owners are rejected.
The test-only APIs remain thin adapters and are excluded from the normal loader.
Protected-source and writer test APIs remain excluded too.

The scenario accepts an optional private `CanonicalAttempt`. It creates role
requests through that object and passes it privately through both named flows
to `Start-AppInstance`, which invokes the checked role launch. Existing callers
without the attempt keep the original request/start behavior. Failure to create
the optional pair keeps the existing diagnostic-omission path and ordinary
launch behavior. No capability is placed in the scenario context, environment,
formatter or serialized output.

The original process creation, started-process tracking, report callbacks,
output readers, cleanup and failure propagation remain in place. This step
grants no writer custody, transfer-integrity result, publication completion or
coordinator authority. Native retirement remains separate from role-resource
disposal; the previous lock-order safeguard is retained.

## Verification

Evidence: `artifacts/production-canonical-roles-20260929/`.

The test-project build passed with 343 existing warnings and zero errors.
The new normal-loader test reproduced the missing production role API on both
PowerShell hosts before implementation.

The new suite covers both hosts, with the normal loader throughout the original
parent/wrapper/GUI chain and the two headless app children. It checks enabled
binding, unconsumed and copied attempts, parent cancellation, both original
report-setup failure paths, private capability propagation, ordinary output
drain and cleanup. Embedded controls reject unknown/swapped roles, copied
requests/reports/attempts and repeated creation or launch. The actual live
scenario call is still asserted to receive no owner.

The initial exploration exposed a fixture omission before app launch: the
normal-loader scenario fixture had not loaded the real payload/environment
readers. A cleanup assertion initially obscured that error. The fixture now
loads those original readers and also checks the scenario's consumed values
against the canonical projection. No production behavior was relaxed to fix it.
Initial results are preserved separately from the final focused run.

Final result: **40 distinct green tests**, no remaining failures or skips:

- 2 normal-loader API checks and 12 final integration cases (the latter took 3m56s).
- 26 compatibility cases, run once (7m20s): 14 prior role controls, 8 original
  scenario valid/default/report-failure cases, 2 attempt-dispatch cases and
  2 protected-source cases. The source checks are local custody fixtures, not a
  new end-to-end or live 256 MiB transfer.

The exploratory run recorded 6 passes and 8 fixture failures. The final run
reran all 12 integration cases after the reader correction; the unchanged
normal-loader controls were not rerun. Embedded assertions and reruns are not
counted as extra distinct tests. The final verifier checks syntax, whitespace,
the exact unchanged Context-only live call, preserved file hashes and recorded
child-process exits.

## Preservation and next gate

Entry verified all 9,500 prior file pins and 635 dirty entries from
`artifacts/production-attempt-gui-dispatch-20260929/FINAL-VERIFICATION.json`
(SHA-256 `f5fe2d410b49303e22eea14488c627dc0a404eeb70cddd67a6298d76b6de81a9`).
Before-images preserve the eight edited existing files and previous test DLL.
Six new repository files, including this report, bring the dirty-entry count
to 641. The final seal records exact changes, test results and process cleanup.

Next: connect the protected source and exact completed destination to these
original roles, then preserve custody through the wrapper/parent and original
primary-analysis handoffs. Keep the live entry gated throughout. Once that
integration is verified, the reviewed clean checkpoint, fresh portable build
and strict escalated preflights precede the separately scoped live-256 run.

The second coordinator-published `file_tuna_v4` route is still required;
completion/SHA or diagnostic ownership alone is not route-cycle proof.
