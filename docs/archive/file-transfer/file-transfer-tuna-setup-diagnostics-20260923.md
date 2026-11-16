# Bounded Tuna setup diagnostics — 2026-09-23

## Scope

Implements the evidence-retention step from
`file-transfer-initial-tuna-dial-rca-20260923.md`. The current-head live attempt
failed before transfer, with a retained TCP refusal but no per-path endpoint
correlation. This change supplies diagnostics for the next attempt; it does not
establish or fix the refusal's transport root cause.

## Implementation

- Each `NknTunaSidecarProcessOwner` owns one `NknTunaSetupDiagnostics` instance.
  It has no transport/coordinator reference, authority token, or runtime callback.
- The listener's actual stdout subscription retains only existing
  `provider_path_state_changed` fields: index, current/previous endpoint hashes,
  usable flag, allowlisted state reason, and observation time. The existing
  current-owner guard and subsequent runtime handling are unchanged. Late output
  is diagnostic-only and stays associated with the captured old process owner.
- The dialer's actual stderr subscription recognizes the pinned SDK v0.2.6
  per-path dial-error format. It emits only an endpoint fingerprint and a bounded
  refusal/timeout/other category. The fingerprint matches Go `shortHash`: the
  first eight SHA-256 bytes of literal `ip:port`, in lowercase hexadecimal.
  Raw endpoint/error text, unrelated JSON fields, and peer/wallet data are not
  included in these new records. Existing generic diagnostics are unchanged.
- Process role, PID, and the owner's generated GUID correlate records. The GUID
  uses hyphenated `D` format: the existing redactor masks its compact 32-hex
  representation. No redactor exception or broader secret exemption was added.
- Each owner is limited to 64 detail/invalid-input records, plus at most one
  truncation marker and one at-stop snapshot. Input is bounded to 4,096
  characters; malformed/oversized recognized input yields a fixed invalid
  category, not an invented path error. Unknown output is ignored by this helper.
- The at-stop snapshot explicitly distinguishes no observed details from
  observed details. It is not a promise that asynchronous output is drained;
  late diagnostic records are marked `after_stop=1` and retain the same cap.
  Sink failures cannot throw into process I/O or cleanup.

No Go/SDK update, connection probe, readiness change, retry change, provider
selection change, route publication, progress change, or new authority path.
Fingerprints are correlation aids, not a claim of irreversible anonymity.

## Verification

Artifacts: `artifacts/tuna-setup-diagnostics-20260923`.

- Two tests through the real listener callback reproduced missing evidence for
  current and stale owners before implementation (`diagnostics-red.trx`).
- The first implementation passed 24 of 27 new cases. Three retained-log tests
  exposed compact owner-GUID redaction (`diagnostics-green.trx`).
- After changing only diagnostic GUID representation, the three affected
  callback cases and the owner-isolation/cap case passed
  (`diagnostics-owner-green.trx`). Latest results for all **27 distinct new
  cases are passing**; unrelated parser cases were not rerun for this format fix.
- **74 directly affected existing cases passed** in one regression run:
  `NknTunaLaneOwnershipTests`, `NknTunaAccelerationLaneOriginTests`, the five
  listener-sidecar diagnostic tests, both delivery-trace stdout adapter cases,
  and the retained full-capture test (`affected-infra-green.trx`).
- Coverage includes real redacted log retention, fixed hash vectors, unknown and
  malformed input, private-field exclusion, concurrent cap ordering, late output,
  replacement isolation, and unchanged readiness/ownership behavior.
- All 132 prior live pins remain unchanged. Of the 16 RCA pins, only the two
  intentionally instrumented C# adapter sources changed. SDK, sidecar, ownership
  implementation, previous reports, and original primary evidence are unchanged.

Total latest selected results: **101 distinct cases pass**, none skipped.
Build warnings include unavailable NuGet vulnerability metadata in the restricted
sandbox and existing compiler/analyzer warnings. No aggregate contract/analyzer
lane was repeated: their code and contracts did not change.

## Next gate

Review/checkpoint this bounded change locally, rebuild the portable package,
verify source/package freshness, run escalated strict preflights, then run one
`second-transfer-after-reactivation` attempt. Keep setup/goodput reruns disabled
and stop at a new first failure. Do not reuse the old executable, rewrite prior
analysis, or run the full matrix yet.

This implementation milestone does not commit, push, rebuild the portable, or
launch a live scenario. The earlier retry-retirement runtime fix remains
live-unverified, and the original refusal remains unresolved.
