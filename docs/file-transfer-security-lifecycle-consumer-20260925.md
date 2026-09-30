# Recorded security-context span consumer

2026-09-25. Branch `v0.7.0`, base HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.

## Implemented scope

Implemented the bounded diagnostic from
`file-transfer-security-lifecycle-consumer-contract-20260925.md`. The separate
test-only opt-in is `NLINK_PUBLICATION_SECURITY_LIFECYCLE_CONSUMER=1`; the normal
consumer remains unchanged when this is absent. No runtime, semantic retention,
authority catalog, recovery classifier or acceptance rule changed.

The new matcher uses the eight already selected envelope diagnostics and the
two original verified writer endpoints. Each independently completed authority
view supplies its selected positions. The original full capture supplies lifecycle
records; compact authority membership does not expand. Complete source coverage,
original references, native ownership and writer custody are checked before and
after the query and controls. No input file is reread, capability exported,
reader reopened or authority evaluation added by the consumer.

The matcher checks canonical local identities and positive Int64 values,
captured selected statuses, unique matching install/retire markers, exact tuple
continuity, strict writer-local bracketing and conflicting identity reuse anywhere
in the capture. Canonical lifecycle spelling is required after case-insensitive
recognition, so malformed casing cannot hide a record. Other installations cannot
overlap the selected span or reuse its transport/generation or installation
identity. Unrelated contexts and later non-overlapping open/closed contexts remain
possible; they do not lend identity or retirement evidence to the selected one.

The result is `matched_recorded_security_context_spans`, with exact recorded
marker/use origins and full/compact agreement. It explicitly keeps
`SecurityLifetimeProven=false` and `RuntimeRetirementProven=false`. It describes
log order and captured identities, not execution-time key validity or callback
effects. Optional omission preserves the prior yield, reciprocal, rejection and
dispatch results, the original primary outcome and its existing carrier status.

All existing bounds remain: 4,096 full records, 256 boundaries per view, 64
operations per boundary, 64 deduplicated witness lines across the entire chain,
and the same 20-second cumulative stopwatch, plus inherited native limits.
Post-pass review replaced repeated searches for other installation companions
with a bounded per-installation index and added elapsed-work checks during the
scan. No budget was increased or restarted. Controls clone detached field maps;
unchanged raw Snapshot strings and original inputs are not serialized or mutated.

## Verification

The first integration failed on PowerShell 5.1 because `SecurityLifecycleStatus`
was absent, before the opt-in was connected. PowerShell 7 was not reached in that
intentional red run. The first connected integration passed on both hosts, and
the final focused rerun after the work-bound refinement also passes on both.

Accepted final original-capture case:
`artifacts/publication-security-lifecycle-consumer-20260925/3951a7d05d9647b29bd85d94cdcb4746/`.

- 343 full records, 162 compact authority records and 71 passing boundaries per
  view; full/compact parity and unchanged-input checks pass.
- Exactly three original evaluations and original evaluator callers remain.
  The fixture deliberately lacks the exact Tuna receive; its strict
  `missing_exact_tuna_receive` result remains unrecovered.
- Two recorded contexts cover the eight selected diagnostics. Sender install/
  retire sequences are 11/151 within capture endpoint 153; receiver sequences
  are 13/228 within endpoint 230. The lifecycle source origins are 17/341 and
  16/343 respectively. These are observed values, not hard-coded prerequisites.
- The combined original witness set contains 24 lines. Forward and reciprocal
  yield matches, rejection-chain match and the captured dispatch no-match remain
  intact. The original receiver capture endpoint is not redefined as retirement.
- Seven positive and 71 negative detached controls pass on each host, including
  both peers' missing/duplicate markers, identity and generation conflicts,
  malformed fields/statuses/casing, overflow, same-establishment replacement
  identity, intervening/enclosing contexts, captured text recorded after
  retirement, endpoint failures, origin/selection/parity failures, missing work
  context and shared witness overflow. These are query controls, not new
  authenticated histories or runtime retirement tests.

The prerequisite-omission integration also passes on both hosts:
`artifacts/publication-security-lifecycle-consumer-20260925/d73fd6fcb83c49ba93f89ab62735b4f1/`.
With dispatch capture disabled, the new query reports
`security_span_requires_dispatch_capture`, publishes no span result, and preserves
the earlier yield/reciprocal/rejection results and strict primary failure.

The existing dispatch integration passes with an added explicit assertion that
the lifecycle consumer is default-off:
`artifacts/publication-dispatch-window-20260925/42d3d78a7b9f4d77a39c394d4d3955bd/`.
Its original 10 positive/exclusion and 50 negative controls pass on both hosts.
These omission/default-off paths do not execute the later refined matcher and
did not require a redundant rerun after that isolated refinement.

The final verification set is three passing test cases, each covering both
PowerShell 5.1 and 7. Receipts under
`artifacts/publication-security-lifecycle-consumer-20260925/results/` are:

- `security-span-red.trx`: intentional absent-result reproduction.
- `security-span-green.trx`: passing intermediate integration before work-bound
  refinement; retained as development evidence, not the final implementation seal.
- `security-span-final.trx`: final positive integration and 78 controls.
- `security-span-preservation.trx`: passing omission and default-off integrations.

PowerShell parsing and whitespace/diff checks pass. No aggregate suites, unrelated
runtime lanes, portable rebuild or live GUI/NKN test were run.

## Preservation and next boundary

Only four pre-existing files changed: the test declarations, observer opt-in,
test-only probe and dispatch default-off assertion. All 61 other pre-existing
dirty-file contents are unchanged. Of the preceding review seal's 19 pins, only
the three intentionally edited support scripts changed; the review seal itself
is unchanged. New files are the matcher, controls, two integration scripts and
this document. The implementation seal is
`artifacts/publication-security-lifecycle-consumer-20260925/FINAL-VERIFICATION.json`.

This completes recorded lifecycle consistency, not the broader stale-safety
contract. Next, assess the remaining execution/callback-effect proof against
existing producer guards and tests before proposing any classifier or acceptance
change. Do not infer absence of effects, exact Tuna delivery or Coordinator
publication retirement from these successful diagnostics. No live-256 readiness
is established here, and no live run, commit or push occurred.
