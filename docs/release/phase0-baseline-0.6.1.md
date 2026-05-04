# Phase 0 Baseline - 0.6.2 Hardening

Date: 2026-05-01

This note captures the pre-hardening baseline for the 0.6.2 follow-up work. It is intentionally descriptive: no production behavior was changed in Phase 0, and no tests were added to lock in behavior scheduled for Phase 1 removal.

## Commands Run

Preferred full baseline:

- `dotnet test -c Release`
  - Result: failed immediately in the sandbox with exit code 1 and no captured diagnostics.
- `dotnet test -c Release --no-restore -m:1 -p:UseSharedCompilation=false`
  - Result: failed after rerunning outside the sandbox.
  - Totals across the solution: 1,526 passed, 5 failed, 34 skipped, 1,565 total.

Targeted fallback baseline:

- `dotnet test tests\nLink.SmokeTests.Core\nLink.SmokeTests.Core.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false`
  - Result: failed; 602 passed, 2 failed, 1 skipped, 605 total.
- `dotnet test tests\nLink.SmokeTests.Gui\nLink.SmokeTests.Gui.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false`
  - Result: passed; 232 passed, 0 failed, 14 skipped, 246 total.
- `dotnet test tests\nLink.SmokeTests.RemoteControl\nLink.SmokeTests.RemoteControl.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false`
  - Result: passed; 87 passed, 0 failed, 0 skipped, 87 total.

Environment notes:

- Sandboxed MSBuild could not update existing `bin`/`obj` outputs, failing with access denied on generated files such as `nLink.Core.deps.json` and generated editorconfig files.
- A fresh isolated intermediate directory required restore and hit blocked NuGet network access, so the trustworthy baseline used existing restored assets with `--no-restore` outside the sandbox.
- The early parallel targeted run was discarded as a product signal because it caused package-assets cache contention.

## Existing Failures

Reproduced on individual rerun:

- `NLink.SmokeTests.TestArchitectureContractTests.BugReportTemplates_RequestCurrentSupportEvidence`
  - Failure: bug-report template does not contain `v0.6.1`.
- `NLink.SmokeTests.TestArchitectureContractTests.ActiveDocsAndTemplates_DoNotHardCodeStaleReleaseVersions`
  - Failure: `README.md:13` intentionally mentioned the previous H.264 baseline with an explicit stale version, and the contract flagged it.
- `NLink.SmokeTests.BridgeConnectionLifecycleTests.Bridge_Startup_HealthCheck`
  - Failure: bundled bridge startup is blocked by `manifest_missing`.
- `NLink.SmokeTests.SessionFileTransferPauseTests.InboundPauseDuringReceiving_SuppressesCreditAndMissingRangesUntilResume`
  - Failure: timed out waiting for the paused inbound transfer condition.

Did not reproduce on individual rerun:

- `NLink.SmokeTests.ScreenSharePreviewIntegrationTests.HelpeePreview_ToggleOnOff_Repeatedly_DoesNotCrash_AndCleansUp`
  - Full-suite failure: timed out waiting for preview frame state.
  - Individual rerun: passed, so treat this as flaky or environment-dependent until proven otherwise.

## Coverage Inventory

Coverage already present and should not be duplicated:

- V4 data-frame codec coverage: `FileTransferDataFrameCodecTests` covers V4 manifest/state/chunk-batch/complete/cancel/error roundtrips, packed batch limits, invalid missing ranges, oversized packed batches, and legacy frame rejection.
- Session verification coverage: `SessionVerificationCodeDerivationTests`, `SessionVerificationRuntimeStateTests`, `SessionIdentityAndVerificationTests`, `HelpeePageViewModelLifecycleTests`, and `HelperPageViewModelLifecycleTests` cover deterministic derivation, transcript sensitivity, runtime mirroring, helper/helpee display parity, hiding after approval, and fallback hiding in the GUI.
- Bridge integrity coverage: `WindowsBridgeLifetimeTests` covers built-bundle manifest/hash checks, hash mismatch detection, every non-OK startup-guard status, and blocking mismatched bundles before launch.
- Receive storage safety coverage: `FileTransferSecurityGuardTests` covers missing capability, invalid names, traversal, reserved device names, oversized files, 25 GiB metadata allowance, session/helper mismatch, chunk size, duplicate-name numbering, and temp preservation when finalization races.
- V4 transfer coverage: `SessionFileTransferV4ReceiverTests`, `SessionFileTransferV4SenderTests`, `SessionFileTransferPauseTests`, and `NknFileTransferTransportTests` cover sparse receive, repair, missing ranges, mixed screen-share pacing, pause/resume, V4 bulk/control transport routing, splitting, unknown transfer rejection, and busy-guard cleanup.

## Characterization Gaps

Phase 1 gaps:

- JSON fallback: `FileTransferDataFrameCodec.TryDeserialize` still accepts JSON-looking V4 frames by default. Do not add a permanent passing test for this behavior; replace it with binary-only release-mode tests in Phase 1.
- Verification UX: the approval-time five-symbol sequence is visible and non-blocking by design. Existing tests cover display and hiding, but Phase 1 should preserve freshness/stale-state and sensitive fallback hiding while leaving approval non-blocking.
- Risky inbound files: no executable/script-like extension classifier or warning was found for inbound offers. Add warning behavior and tests in Phase 1.

Phase 2 gaps:

- Disk-space preflight: no receive-path available-space check was found before inbound storage creation.
- Startup temp cleanup: receive temp files use the `.nlink-*.part` naming pattern and cleanup-on-dispose exists, but no startup or stale orphan cleanup was found.

Tests expected to fail after hardening expectations are added:

- Release-mode JSON V4 payload rejection.
- Risky inbound filename warning/confirmation.
- Insufficient disk-space receive rejection.
- Stale `.nlink-*.part` orphan cleanup.

## Exit Status

Phase 0 baseline is established with known failures separated from future hardening expectations. No runtime behavior was changed. Next implementation should start with Phase 1 tests and fixes for binary-only live decode, risky-file warning, and verification display regressions, while keeping the approval flow non-blocking.

## Follow-Up Test Fix Verification

The baseline failures above were fixed after the initial Phase 0 inventory. Verification command:

- `dotnet test -c Release --no-restore -m:1 -p:UseSharedCompilation=false --logger "trx;LogFileName=SolutionFinal.trx" --verbosity minimal`
  - Result: passed.
  - Totals across the solution: 1,531 passed, 0 failed, 34 skipped, 1,565 total.
