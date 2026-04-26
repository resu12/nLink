# Screenshare Stabilization Protocol

## Purpose

The goal of screenshare work is usable screensharing.

User-visible stability is the primary success criterion. Better counters, cleaner attribution, or more sophisticated recovery logic do not count as success unless helper rendering stays usable.

## Core Rules

- Keep one immutable "last known usable baseline" for screenshare work.
- Start every new screenshare branch from that baseline, not from the latest experiment.
- Use one hypothesis per branch.
- Freeze non-target layers for each pass and list them explicitly in the branch plan.
- Do not patch a failed branch forward. Abandon it or reset to baseline.
- Any new state machine, grace window, phase, or recovery mode must replace an older one rather than stack on top.
- Prefer simplification over additive recovery logic.

## Required Change Card

Every screenshare task or branch must begin with this short change card:

- Bottleneck being targeted
- Touched subsystem(s)
- Frozen subsystem(s)
- Expected mechanism proof
- Expected usability proof
- Rollback condition

If this card cannot be written clearly, the change is too broad.

## Exploration vs Promotion

### Exploration

Exploration runs are for learning only.

- They may fail.
- They do not justify keeping a change.
- They are allowed to test one hypothesis quickly.

### Promotion

Promotion runs are required before a change is kept.

Not every scratch run needs the full gate. Every kept screenshare change does.

## Promotion Gate

A screenshare change may be kept only if all of the following pass:

### Mechanism Proof

- The intended new mechanism clearly activates in logs or counters.
- If the intended mechanism stays inactive, the change is treated as ineffective.

### Deterministic Proof

- Focused tests for the touched subsystem pass.
- One deterministic local validation run passes.

### Live Proof

- 3 fresh 30-second NKN soaks pass consecutively from the same branch.
- Do not cherry-pick the best soak.

### Usability Proof

- No helper freeze
- No 10-second no-progress failure
- `visible_apply_ratio >= 0.98`
- `helper_apply_ms avg <= 550`
- `reassembler_loss_count <= 15`

### Guardrail Proof

- `effective_media_plane_active = 1`
- `steady_state_used_control_fallback = 0`
- Frozen layers do not regress

## Review Order

Review every screenshare candidate in this order:

1. Did the intended mechanism activate?
2. Did user-visible behavior improve?
3. Did any frozen layer regress?
4. Is the branch simpler than what it replaced?

If the answer to 4 is no, the branch needs explicit justification or should be discarded.

## Stop Rules

- If a branch fails the promotion gate, do not build another fix on top of it.
- If a branch improves accounting but not usability, it is not successful.
- If the mechanism never activates, the branch is invalid.
- Repeated failure in the same approach means simplify or rework the model instead of adding more heuristics.

## Current Milestone Baseline

Use this milestone state as the default planning frame for current screenshare work:

- Phase 1 complete: dedicated recovery receipt protocol is landed and validated.
- Phase 2 complete: helper recovery receipt publication is explicit and minimal.
- Phase 3 complete: helper recovery uses the hard keyframe-only gap model.
- Phase 4 complete: sender recovery is receipt-driven and separated from advisory pressure and promotion.
- Phase 5a complete: sender-side deconcentration seams are landed without behavior regression.
- Phase 5b complete: coordinator small-file cleanup is landed and runtime-validated.

Interpret milestone status this way:

- "Recovery model complete" means phases 1 through 4 are done.
- "Architecture partially deconcentrated" means phase 5a is done.
- "Architecture fully deconcentrated" means phase 5b is complete and the coordinator-size cleanup objective is met.

Default decision rules from this baseline:

- Do not reopen phases 1 through 4 unless a later branch proves a concrete regression in those mechanisms.
- Treat phase 5 as complete unless a later branch proves a concrete structural regression.
- Any new screenshare branch should start from the current stable post-phase-5 baseline.

## Phase 5 Completion Proof

Phase 5 completion is established by all of the following:

- behavior-preserving regression suites green
- DEVLOCAL recovery-receipt scenario green
- at least one fresh NKN soak with no regression in:
  - receipt-driven completion
  - `recovery_completion_accounting_mismatch = 0`
  - same-tuning `reduced/catch_up` behavior
  - point-2 guardrails

Current closeout state:

- phase 5a extracted seams are landed
- phase 5b coordinator small-file cleanup is landed
- the coordinator-size cleanup objective is met

## Track B Local Closeout State

Track B local runtime work is parked at artifact baseline `20260423-164328`.

That closeout baseline established all of the following:

- the behavior-first gate passed for the current local branch state
- the latest local classification was `steady_external_delivery_latency`
- local helper apply, reassembler, C# dispatch, JS listener, `ws`, sender bridge, and bridge transport-health churn were no longer the dominant bottleneck

Default decision from this point:

- do not reopen local Track B runtime work unless the external transport reliability lane later proves a narrow code-owned bridge or connection-policy fix
- otherwise move the main repo workstream to Track C
- treat the remaining latency investigation as a separate external transport reliability lane, not as another helper-side or bridge-local Track B branch

## Current Default Direction

Until explicitly changed by a new plan:

- Preserve the real media lane.
- Keep steady-state control fallback off.
- Prefer deterministic recovery over layered runway, corridor, or follower behavior.
- Treat helper progress as factual input, not recovery ownership.
- Favor simpler recovery primitives over sophisticated continuity salvage.

## Track D Operability Note

Track D is about simplifying how operators validate, collect, and interpret screenshare evidence. It does not reopen Track B latency tuning by default.

Use `docs/screenshare-operability.md` as the top-level operator model. Use this protocol only when planning a screenshare runtime behavior change that needs promotion criteria.

Track D is closed with `tools\ScreenShare-Ops.ps1` as the only screenshare operator entry point, `screenshare-operator-verdict.txt` as the first-read live evidence artifact, and app Diagnostics / Save Hang Report as the first support capture surfaces. Retained Track B analyzers stay available as closeout evidence only.

## Related Docs

- `docs/screenshare-operability.md`
- `docs/screenshare-soak.md`
- `docs/release/rc-guardrails.md`
- `tools/Run-ScreenShareNknSoak.ps1`

Use those documents and tools for validation details. This protocol defines how screenshare work is chosen, judged, and either kept or discarded.
