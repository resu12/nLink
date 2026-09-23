# File fallback projection retirement: implementation and verification

Base: `33b57dd1d203e9408437a91012cc3eb755cc68ae`, branch `v0.7.0`.
Status: implemented locally; focused regressions pass. The affected verification
gate is **not green**: three stale fixtures and one Debug stack overflow also
reproduce on an independently built archive of the unmodified base. No commit,
push, portable rebuild, live scenario, or full matrix was performed for this patch.

## Missing effect and implemented boundary

The original full-matrix second-transfer evidence is retained under
`artifacts/full-gui-matrix-20260923-e35fbbf1/live/20260923T080838Z`.
Its first transfer reactivated Tuna-v4, but the next transfer selected fallback-v6
because the old session file-fallback projection remained active. The preceding
RCA is `docs/file-transfer-full-matrix-20260923-rca.md`; its historical status and
the original verdict are intentionally unchanged.

The implementation reuses the coordinator's existing route-transition
disposition command. It does not add another authority evaluation or use route
hints as authority:

- The current transition pause binding captures the exact fallback state object.
  The committed fallback disposition records its exact publication token.
- A committed fallback-v6 to Tuna-v4 disposition can retire that file projection
  only at the exact current binding, without a retained predecessor, and with
  matching source publication, session, transfer, direction, terminal, leg and
  transport epoch. Successor publication/commit and owner generations are checked.
- A tombstone on that object removes the file-fallback selection/suppression
  projection. It does not clear the screen lane, counters, pending control-plane
  owners, or the whole fallback state, and does not grant Tuna readiness.
- Old observations, rejected/stale commands, duplicate dispositions, missing
  bindings, terminal/supersession races and newer local/peer stops cannot retire
  a newer state or reopen the retired projection. A fresh user stop still creates
  its existing new state. A new exact coordinator-committed fallback may reopen
  the file projection.

Unchanged: route resolver precedence, protocol invariants, payload/chunk policy,
throughput floor, queue/concurrency, retries, GUI timing and analyzer acceptance.
Fresh next-transfer admission/readiness still belongs to the existing coordinator
path. Real second-transfer Tuna selection remains unverified until a new live run.

## Verification retained

Artifacts: `artifacts/fallback-projection-retirement-20260923/tests`.

- `fallback-projection-red.trx`: two fixture-setup failures, not the semantic red.
- `fallback-projection-red-corrected-fixture.trx`: both directions reproduce the
  stale fallback flag after committed reactivation, before the production fix.
- `fallback-projection-final-guards.trx`: **37/37 pass**: 27 retirement controls
  and ten existing DevLocal binding/availability checks. Controls include both
  directions, mixed-lane preservation, observation-only behavior, a fresh stop,
  a new committed fallback, and 23 stale/mismatched/terminal cases.
- The first affected run retained 108 passes and eight test-factory reflection
  failures before its host aborted. Restoring the original two-argument helper
  signature fixed those failures; the ten DevLocal checks above include them.
- The unfinished-run retry identified a stack overflow. A subsequent run excluding
  that isolated crash completed 42 cases: 39 pass, three fail. No aggregate pass
  is claimed. Latest per-case audit of the original selection: **157 selected,
  153 passing, three failed, one uncompleted crash**. Overlapping run totals are
  not summed. All six second-Tuna-publication service controls have passing results.
- Default operational-log writes were denied in the first sandboxed run. Later
  tests used the supported workspace-local log configuration. The stack overflow
  persists with writable logs; log denial is not its RCA.
- `git diff --check` passes. The previous helper target's 88 artifact pins and
  original full matrix's 233 pins verify unchanged.

## Four pre-existing verification prerequisites

An untouched `git archive HEAD` copy was restored and built in the artifact
directory with the same Debug configuration. Its three expectation tests fail
at the same assertions, and the isolated stack test aborts with the same named
call chain. See `fallback-projection-baseline-expectations.trx` and
`fallback-projection-baseline-stack.trx`. This establishes baseline reproduction,
not permission to ignore these tests or claim a live-runtime failure.

1. `FileTransferRouteStatus_PostTunaFallbackReactivationWaitsForFallbackOwner`
   expects lower-level fallback-owner diagnostics, but the fixture lacks the
   paired-tail-yield install receipt. The earlier retry-authority guard correctly
   returns `fallback_paired_tail_yield_install_receipt_missing`. Next: prepare
   the intended exact coordinator authority before testing the lower boundary;
   retain a separate missing-authority rejection assertion.
2. `FileTransferFallbackRecovered_UserStopStartsFreshFallbackAfterReactivation`
   expects a recovered epoch observation to synthesize fallback state without a
   committed fallback transition. The log records
   `coordinator_route_transition_commit_required`. Next: establish the committed
   fallback fixture before its recovery observation and preserve fresh-stop checks.
3. `FileTransferTunaActivationPause_PostFallbackRecoveryKeepsFallbackAvailabilityAuthoritative`
   expects an unowned availability broadcast to make the session unavailable.
   The existing guard records `peer_fallback_proof_required` instead. Next:
   construct the coordinator-owned source availability/handoff before the pause
   check; preserve the invariant that observations alone cannot change availability.
4. `OutboundRegularV4WriteHandoff_ProbeFailureInvalidatesOnlyExactCurrentDrain`
   overflows during setup's `ReplaceOutboundDataSessionLocked` call, in retry
   lifecycle snapshot projection. The new retirement path is absent from the
   stack. Retained MinOpts disassembly shows the test reserves `0xE6EC0`
   (945,856) bytes and `AddRuntimeUnlockRetryOperationDescriptors` reserves
   `0x19C00` (105,472) bytes, excluding intermediate frames. This is not evidence
   of recursive calls or a measured general production stack limit. Next: isolate
   the test's repeated large persistent-state reads and setup from its assertions,
   preserving every assertion; verify in ordinary Debug without larger stacks,
   alternate scheduling, optimization switches or changed runtime authority.

These prerequisite fixture corrections are planned, not implemented in this patch.
Complete their focused verification before considering the affected gate green.
Then use a clean local checkpoint, fresh portable package and strict preflights
for only `second-transfer-after-reactivation`. Do not rerun the full GUI matrix
or weaken its second-transfer Tuna-v4 requirement to bypass this gate.
