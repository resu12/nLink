# GUI failure-classification response shape — 2026-09-23

## Evidence and scope

The latest targeted campaign at
`artifacts/post-fallback-retry-retirement-live-20260923-2dde5239/live/20260923T142615Z`
failed initial Tuna preparation before any transfer. The listener started, but
the dial timed out after 25 seconds. This change does not explain or fix that
timeout, validate the retry-retirement runtime change, or make the campaign pass.

Its completed generic failure-classification response contained 39 properties,
missing 37 of the canonical constructor's 74 required properties. The unchanged
strict validator therefore rejected it, retaining `evidence_enrichment_pending`.
The successful enrichment fixture used the canonical constructor, masking the
generic producer's divergent response shape.

## Fix

The generic return now starts with
`New-TunaGuiFileTransferFailureClassificationResult` and overlays the same
observed flags with `Add-Member`. Both generic-only flags are preserved. Phase,
reason, precedence, metrics, evidence matching, and classification completeness
are unchanged. Missing unrelated fields receive the same neutral defaults used
by explicit/indexed responses. No required-property validation was relaxed.

No application runtime, transport, coordinator authority, retry timing,
throughput policy, or GUI scenario behavior changed.

## Verification

Artifacts: `artifacts/gui-classification-shape-20260923`.

- Four new tests exercise the actual generic producer for listener-unavailable,
  terminal, and unknown outcomes, including the completed-response mode used by
  the classification child. They serialize/deserialize the response and invoke
  the real strict validator. They check original flags, extra flags, neutral
  authority defaults, metrics, and rejection of incomplete/partial responses.
- All four reproduced the exact missing-property failure before the production
  change (`generic-red-ready.trx`) and passed afterward (`generic-green.trx`).
  An earlier fixture-only missing dependency failure is also retained.
- Both existing enrichment tests passed, including real-child exit handling
  and timeout/malformed-response preservation.
- The existing explicit-precedence test exposed a stale assertion: commit
  `5638f51b0` changed the missing-successor reason in August, but the assertion
  still expected its July value from `846a2356d`. The checkpoint's extracted
  mapping was reproduced, and the unchanged constructor/validator were checked.
  Only the assertion was updated to the existing detailed reason; the test then
  passed. Other indexed/analyzer tests already expect that same reason.

Latest results: **7 distinct selected tests pass**, with no skipped tests.
Only the failed precedence test was rerun after its one-line correction. No
aggregate suite or live run was needed to verify this reporting-only change.
Build warnings include unavailable NuGet audit metadata in the restricted
sandbox; the deterministic tests completed normally.

## Next gate

Review/checkpoint these changes, rebuild the portable package, run escalated
strict preflights, then one `second-transfer-after-reactivation` attempt. Keep
the full matrix gated and stop at a new first failure. The old live verdict
remains FAIL/setup/incomplete proof; do not rewrite or reclassify its outputs.
The underlying dial timeout remains unresolved, not an established external
network diagnosis. This milestone does not commit, push, or launch a live test.
