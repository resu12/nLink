# Prepared callback: direct effect assertions

2026-09-25. Branch `v0.7.0`, base HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.

Implemented the test-only step from
`file-transfer-callback-effect-safety-review-20260925.md`. Production behavior,
diagnostic consumers, authority, classification and acceptance are unchanged.

## Change and verified scope

Only `tests/nLink.SmokeTests.Core/NknFileTransferTransportTests.DispatchRouteGuard.cs`
was changed in test/source code. Its existing seven-case theory still invokes
the exact prepared callback, including first/second Tuna publication rejection,
omitted diagnostic evidence, and permitted Regular, fallback, Tuna and control
cases.

The effect baseline is now explicitly after successor publication and before
callback execution. Preparation's adapter state is recorded separately; the test
does not require preparation to have been effect-free.

Direct assertions cover:

- Adapter transfer state and current route authority, route hint and diagnostic
  publication values.
- Data-session object identity, scope, generation, availability, availability
  reason/generation, disposal state, session count/next generation, and emitted
  availability-event count.
- Rejected-callback queue frame/byte counts and copied fallback receive counter,
  epoch, file lane/transport-epoch/projection values and pending-proof flags.

The fallback object is mutable, so its relevant values are copied individually;
comparing the same object reference would not detect in-place mutations.
At the existing fallback publication, the fixture calls the existing production
`StartTunaFallbackProofIfNeeded` bookkeeping method. It installs no recovery
worker or pending proof authority and fabricates no Coordinator token.

The allowed Regular-delivery case increments the active fallback receive counter
by one, demonstrating that the measured path is exercised. The control-frame
case also increments it; the permitted Tuna case does not. The second-Tuna
rejected payload cases leave those values unchanged. The initial-Tuna case has
no prior fallback object and does not claim active fallback bookkeeping coverage.

The existing fallback case deliberately tests this specific guard's handling of
a previously prepared v4 frame. Passing it does not certify a v4 fallback route:
the published fallback authority remains protocol 6, and complete protocol,
route-cycle and payload-acceptance contracts are unchanged.

## Verification

Focused final receipt:
`artifacts/callback-effect-assertions-20260925/results/dispatch-effect-final.trx`:
**7 executed, 7 passed, zero failed/skipped**. Operational logs were explicitly
directed to `artifacts/callback-effect-assertions-20260925/final-run/nlink.log`.
The normal sandbox and the existing offline FakeNKN fixture were used.

The first implementation run failed all seven cases before dispatch because the
new reflection read used an interface-typed helper to find a concrete session
field. The test now reads that field through the concrete runtime type, matching
its existing queue-field reads. The initial receipt is retained as
`dispatch-effect-assertions.trx`; it is not a runtime failure or an intentional
red regression. The final run rebuilt the changed tests and passed. No unrelated
tests or aggregate lanes were rerun; shared fixtures were not edited.

All 71 pre-existing dirty-file contents are preserved. The previous review seal
had 15 pins; only the intentionally edited dispatch test differs. The 44 pins in
the preceding security-lifecycle implementation seal remain unchanged.
`git diff --check` passes. Final hashes and preservation evidence are in
`artifacts/callback-effect-assertions-20260925/FINAL-VERIFICATION.json`.

## Limits and next boundary

This proves only the asserted outcomes of these deterministic prepared-callback
executions from their dispatch baselines. Inactive pending-proof flags are not
coverage of active proof completion. The test does not cover the real-adapter
recovery branch, scheduler/reset races, execution-time security lifetime,
publication retirement, durable transfer completion or every callback family.
The original recorded-chain consumers therefore remain diagnostic-only, and
`missing_exact_tuna_receive` is not recovered or waived by this test.

The bounded assertion step is complete. Do not extend it into a general stale
safety verdict. Any next classifier/acceptance decision must retain these limits
and independently satisfy exact Tuna delivery, second-publication, integrity and
terminal obligations. No portable rebuild, live-256 test, full GUI matrix,
commit or push was performed or required for this test-only change.
