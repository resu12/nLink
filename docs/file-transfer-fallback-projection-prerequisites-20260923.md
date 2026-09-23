# Fallback projection retirement: offline prerequisites resolved

Base HEAD: `33b57dd1d203e9408437a91012cc3eb755cc68ae` on `v0.7.0`.
The retirement implementation from the preceding milestone is unchanged.
This follow-up changes only tests. No commit, push, portable rebuild, live test,
full matrix or historical authority reevaluation was performed.

## Result

All four previously reproduced baseline prerequisites are resolved. The final
focused run completes **five cases, all passing**, in ordinary Debug. The fifth
case explicitly verifies the missing-commit boundary in an independent fixture.

`artifacts/fallback-projection-prerequisites-20260923/affected-case-audit.json`
maps every currently selected case to its latest retained result: **158 selected,
158 passing, zero unresolved**. This combines focused runs; it is not a new single
aggregate run. The original selection had 157 cases before the new negative
control. The previous 37 retirement/binding guard passes remain applicable.

## Test corrections

### Retry authority rejection

`FileTransferRouteStatus_PostTunaFallbackReactivationWaitsForFallbackOwner`
retains its strict fallback-v6 route, no Tuna activation and no handoff assertions.
It now checks the actual early rejection:
`filetransfer_runtime_unlock_handoff_held_for_retry_authority`, with reason
`fallback_paired_tail_yield_install_receipt_missing`.

The earlier plan to add authority to reach the three old diagnostic messages was
not viable for this call: `ShouldKeepPostTunaFallbackDataSessionsAvailableDuringTunaActivation`
uses the same predicate as the earlier return. Satisfying that predicate would
skip both branches, not produce the old messages. No production guard was changed.
The test reads its cursor-bounded log tail, not the process-wide recent-log cache.

### Recovered fallback and fresh user stop

`FileTransferFallbackRecovered_UserStopStartsFreshFallbackAfterReactivation`
now has independent committed and uncommitted cases. The fixture builds source
Tuna history and fallback transition through existing coordinator test helpers,
then applies the issued command through the real transport bridge with the exact
data-session generation. It does not install private execution shadows by hand.

Without the commit, recovery evidence cannot create the fallback projection.
With the commit, recovery is observed once and the original fresh-stop and
no-unavailable-event assertions remain. An intermediate attempt replayed the
same recovery object after commit and correctly hit the duplicate-generation
guard; the final independent cases do not reset or bypass that guard.

### Availability ownership

`FileTransferTunaActivationPause_PostFallbackRecoveryKeepsFallbackAvailabilityAuthoritative`
first verifies that an unowned fallback availability broadcast has no effect and
reports `peer_fallback_proof_required`. It then applies a coordinator-issued
fallback commit and observes its publication. The resulting available fallback
session remains available during the rejected Tuna pause, with no new availability
events. The previous expected unavailable state came only from the rejected
broadcast and was not an authorized fixture state. Pause-suppression diagnostics
now name the current missing retry-install receipt, not the obsolete reason.

### Debug test-frame lifetime

`OutboundRegularV4WriteHandoff_ProbeFailureInvalidatesOnlyExactCurrentDrain`
keeps all original transitions and assertions: first-drain invalidation, a distinct
successor drain, exact successor generation, stale first-probe rejection,
mismatched successor-probe rejection, and final exact successor invalidation.

Large persistent-state reads and operation assertions are isolated in local
non-inlined helpers. Only boxed operation snapshots are retained across production
calls. Setup no longer retains the assertion frame. Extracting state reads alone
was insufficient, and separating setup alone exposed the remaining large assertion
frame; both intermediate aborts are retained. The final isolated and five-case
runs pass without larger stacks, different scheduling, Release optimization,
runtime/JIT configuration changes, or production-code changes.

## Evidence and next runtime gate

Artifacts: `artifacts/fallback-projection-prerequisites-20260923/tests`.
`final-prerequisites.trx` is the final 5/5 run. Earlier failed/aborted files are
retained as diagnostic history, not counted as passing runs. `stack-split.trx`
also records the isolated corrected stack test passing.

Static review and `git diff --check` pass. All 33 preceding retirement milestone
pins, 88 helper-target pins and 233 original full-matrix pins remain unchanged.
No authority, route, analyzer, timing or performance threshold was relaxed.

The offline selection is now clear. The next gate is a reviewed clean local
checkpoint, fresh portable build, strict readiness/isolation preflights, then
only `second-transfer-after-reactivation` outside the restricted sandbox.
Require actual second-transfer Tuna-v4 route publication and the normal paired
completion/hash/evidence checks. Do not accept fallback completion as a substitute.
Do not run the full matrix yet. The current dirty tree and old portable package
are not suitable inputs for that live acceptance gate.
