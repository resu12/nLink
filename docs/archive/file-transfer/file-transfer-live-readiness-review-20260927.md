# Targeted live-run readiness review — 2026-09-27

## Decision

**Not yet ready to validate the new owned-payload path in a live run.** The
GUI-work-budget blocker is resolved, but the completed 256 MiB milestone ends
at wrapper-owned payload/native-writer association in an explicit offline test
profile. It does not establish the next parent/analyzer hop or enable that path
at the live scenario dispatch.

The recommended next step is a bounded contract and offline implementation of
**wrapper-to-original-parent adoption of the protected source/destination pair**,
before wrapper writer custody is retired. A portable rebuild or another network
run cannot fill this missing ownership transition. No runtime or acceptance
change is made by this review.

Baseline: `v0.7.0`, HEAD `a653d4063070235c71fe211480aeb37863d9b2a4` plus the
existing worktree. All 415 pins in the GUI-budget implementation checkpoint
verify, including the two latest full-size artifacts. Checkpoint SHA-256:
`bfc08cd9e2f03db03b37020570bf54ccff164cec5ee5a784f86196c4bc2241b1`.
The 102 focused and two Windows full-size passing checks remain valid evidence
for their stated offline scope; they are not withdrawn or broadened.

## Current proof boundaries

| Boundary | Current evidence | Readiness |
| --- | --- | --- |
| GUI to wrapper, including exact native writer association | Latest Windows positive and foreign-origin controls pass; prior PowerShell 7 controls also pass. | Offline milestone complete. |
| Wrapper to original parent, carrying the actual transfer pair | The pair is private to `AttemptPayloadWrapperForTest`; no downstream payload handoff is implemented there. | **First missing edge.** |
| Parent to original analyzer, joining that pair to the selected witness | Native log/diagnostic relay and primary witness machinery exist, but this actual 256 MiB pair is not carried into them. | Later integration gate; do not substitute a different small fixture. |
| Live scenario dispatch | The unique call is still `Run-ScenarioFileTransferTunaHandoffFallback -Context $ctx`. Default loaders exclude the attempt/payload test APIs. | Deliberately not enabled; adding `-FrontierOwner` alone is not a fix. |
| Pre-verdict consumption/acceptance | Selected-failure writer reservation and strict route-cycle acceptance remain intact. | Separate reviewed gate, not implied by hashes or custody. |

The distinction between diagnostic payloads and transfer files remains
important: `LocalFrontierFileManifest.MaximumPayloadBytes` is 262144. Its
diagnostic catalog must not be relabeled or enlarged to transport the actual
268435456-byte source/destination files.

## Why the next handoff needs a bounded contract

Current code makes the lifetime boundary explicit:

- `tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadWriters.cs:30`
  accepts the original terminal completion and binds the wrapper pair to the
  exact `FrontierParentCustody`. Its lifetime watch closes the pair when that
  writer custody is canceled. All three authority/acceptance flags on the pair
  remain false (`FrontierAttemptPayloadWrapper.cs:321`).
- The positive writer fixture then deliberately disposes that writer custody
  and independently verifies both payload handles close. Its `Writers` seam
  replaces the ordinary terminal-take statement; it does not prove downstream
  relay adoption (`FrontierAttemptDispatchWrapper.ps1:67` and
  `FrontierAttemptPayloadWrapperProbe.ps1`, `Writers` branch).
- The ordinary wrapper calls `TryOfferAndWait` and then `Forward`
  (`tools/Run-FileTransferTunaGuiSmoke.ps1:837`). The existing native log relay
  duplicates its own pins, and `OfferToRelay` closes the original writer
  custody in its `finally` (`tools/FileTransferOps/LocalGuiWriterRelayTransfer.cs`).
  `FrontierParentCustody.Close` disposes its channel, which signals cancellation
  (`LocalGuiWriterParentCommit.cs:154`, `LocalFrontierParentCommitChannel.cs:94`).

Consequently, appending payload export after ordinary writer relay/cleanup
would be too late. A parent must acquire independent protected handles before
the last wrapper-owned copies retire, and must accept them only through the
same original attempt and native writer/completion lineage. Expected one-shot
handoff retirement must be distinguished from cancellation without granting a
blanket exception for stale owners or trusting labels.

The next contract should specify one separate bounded transfer-file handoff,
its original parent/child process identities, provisional adoption, acknowledgment,
commit, successful wrapper exit/take and cleanup. Reuse existing native relay
lifetimes where valid; do not reopen by path/PID, manufacture authority from
JSON, issue extra evaluations, reset clocks or raise the diagnostic payload cap.
The parent/relay five-second work limits remain unchanged by the GUI allowance.

Required offline evidence: original two-file positive adoption on both hosts;
wrong/copy/foreign owner and writer lineage; duplicate/early take; canceled or
abnormal wrapper exit; interruption around provisional adoption/commit; unchanged
budgets; and independent handle release after retirement. The positive must
continue past the current fixture's deliberate cleanup and demonstrate parent
protection while the wrapper's copies are gone. Primary/analyzer consumption
should remain a subsequent bounded step, not be inferred from parent adoption.

Existing `TryBeginFrontierPrimary` / native witness APIs are not absent and
should be reused where appropriate. However, the separate selected-terminal
test adapter refuses `frontierRequested`, and the legacy writer/primary APIs
also refuse that profile. Clearing this flag or reopening the legacy writer
would bypass the boundary, not integrate the actual pair. The selected-failure
pre-verdict reservation remains at `TerminalPreVerdictObserver.ps1:44`.

## Deferred live execution gate

After the missing custody/consumer and live-dispatch contracts are separately
implemented and verified:

1. Run only regressions affected by those changes and their directly affected
   Core/Infra/analyzer lanes; at most one broader contract lane. Do not repeat
   the already passing budget lanes without a relevant change.
2. Produce a fresh portable package using `installer/Build-Portable.ps1` and
   verify its source/binary/bridge provenance. The current canonical executable
   is dated **2026-09-13**, not a fresh build of this worktree.
3. Use `Run-FileTransferRouteAcceptance.ps1` in
   `phase5-analyzer-gui-acceptance` mode, select only
   `regular-v4-live-activation-off-on-off-256mb`, and require
   `StrictCampaignPreflight`. Run the bridge/isolation checks and live scenario
   outside the restricted sandbox with explicit escalation. Strict mode disables
   the filtered-run preflight quarantine continuation; never bypass a failed
   preflight or enable fake/dry-run/skip-preflight modes for live evidence.
4. Retain the canonical Regular4 → Tuna4 → fallback6 → Tuna4 → fallback6
   requirement, including a second coordinator-published `file_tuna_v4`.
   SHA OK, fallback completion, cleanup or near-threshold performance cannot
   replace correctness evidence. A full matrix is not the next step.

No network readiness verdict is claimed: wallet contents, bridge connectivity,
paid transport and GUI preflights were not exercised or modified in this review.

## Verification and preservation

- Four focused default-profile checks passed, zero failures/skips, on Windows
  PowerShell and PowerShell 7: attempt APIs remain absent, mixed profile loading
  rejects, and wrapper-payload APIs remain absent. Receipt:
  `artifacts/live-readiness-review-20260927/results/default-profile-guards.trx`.
- Read-only AST checks report zero parse errors in the GUI, wrapper, acceptance
  and portable-build scripts. The unique live scenario call at GUI line 42981
  has only the `Context` parameter.
- No source/test edits, build, portable package, live run, full matrix, commit
  or push. Earlier artifacts and reports remain unchanged. The recovery seal is
  `artifacts/live-readiness-review-20260927/FINAL-VERIFICATION.json`.
