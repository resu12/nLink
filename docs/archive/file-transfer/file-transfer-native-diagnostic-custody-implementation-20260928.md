# Native diagnostic custody integration: implementation, run and probe RCA

Date: 2026-09-28  
Branch: `v0.7.0`; HEAD: `a653d4063070235c71fe211480aeb37863d9b2a4`  
Status: test-harness integration implemented; one run reached primary capture but failed in the new probe's summary writer. That probe defect is fixed and covered deterministically. **Post-fix native integration remains unverified.**

## Authorized scope and changes

The user approved the bounded implementation and one fresh 168 KiB original-pair run proposed in `file-transfer-native-diagnostic-custody-review-20260928.md`.

- Added a separate `OriginalApps_CarrySmallFrontierReceiptIntoPrimary` test/profile. It reuses the unchanged eight-chunk schedule and rejects other scenarios or actual transfer-file-pair hooks.
- Opted only that profile into the normal full semantic catalog and native GUI copy/persist/prepare/complete sequence before shared-owner retirement.
- Passed the normal original writer context and relay custody into the existing RouteAcceptance retained-analysis path. The primary worker uses the default native loader, not the transfer-pair test extension.
- Added read-only checks of the already-captured native receipt, original evaluation pair, completion boundary/hash, and original manifest completion/marker physical ranges. The marker need not appear in the semantic catalog; its range is checked from the existing native facts, not an invented semantic row or selected witness.
- Added guarded probe/error handling that preserves original reports and disposal, counts the existing conditional resolver call, and rejects any unexpected supported failure. No second capture, evaluator, correlator or witness selection is introduced.

Five existing test/harness files changed; five test/support files were added. No runtime, production-tool, acceptance, coordinator, semantic catalog, timer, packetization or canonical 256 MiB pair assertion changed. The separate fixture-held 256 MiB source is still not the source of the 168 KiB transfer.

## Verification

21 focused checks pass: 10 new guard/profile cases, four existing strict payload/default-loader checks, and seven new direct cleanup/reporting regressions. After follow-up edits, only the affected cases were rerun.

The first guard test version incorrectly placed its mock custody in a private parent scope; correcting the fixture scope made all six guard cases pass. A rebuild encountered a sandbox-denied Avalonia telemetry log outside the workspace. The package's target condition was inspected, and subsequent sandboxed test builds used invocation-only `-p:UsedAvaloniaProducts=` to skip that telemetry target. No repository build configuration changed. Existing compile warnings remain.

The Debug harness was rebuilt before the single escalated original-pair integration. Exactly one integration ran; no second transfer, live-256, portable GUI package run or full matrix ran.

## Original run evidence

Directory: `artifacts/native-diagnostic-custody-implementation-20260928/pwsh-frontier-schedule-7c2544fd39774597a9ffa703788716b5`.

- Both native peers report 172,032 bytes and SHA-256 `67331c29dda482392a6413e0b35c3a7fa7692b5db1bfd1496da5575752f49a0b`. The saved destination independently matches.
- Receiver stdout line 130 reports `PublishedBeforeOriginalDeadline`.
- GUI copy, persist, prepare and complete all returned true. Original wrapper completion, source retirement and role-writer checks passed.
- The primary emitted `original parent analyzer evaluations=3 nativeReads=1`. Its native source had 1,294 events/origins, processed in 21 bounded origin batches. Before/after ordinary source hashes match.
- The Capture-stage probe completed without an error: exactly one original native receipt and 11 native pins; original receipt bytes/hash/schema; both original completion evaluation boundaries and native completion/marker ranges.
- Operational log: 1,694 lines, SHA-256 `21ff1acddf4c1f9b1eac49d131f68c4bdb47f7d008524a835670862887198ea4`.
- Marker at line 1693: observation `c206a97cc062471c9218a357afd321f4`, 6,533 bytes, payload SHA-256 `3099d8b0f104ca6be580378d1db7ea620aa5b9d8b0fce6c3e2d214e4360edc98`.
- Original completion line 1476, receiver sequence 668, has the marker's exact raw UTF-8 hash `59f8c867167ef00ff03e842463df412032720a90d4e469341d62be5fe8d8f8fc` (without newline).

The original full/compact snapshot artifacts remain authoritative, stream-complete and evidence-pass; parity passes. The original operator result is **INCONCLUSIVE / incomplete_proof**, hard-failure count zero, first proof-chain failure `(none)`, and live route-epoch proof `fail`. Completed analysis artifacts do not mean accepted route-cycle proof.

The final parent receipt correctly remains **failed**, exit code 1, because of the test-probe failure below. The absent proof-chain failure implies the unchanged production witness branch is ineligible; the probe's final counter/cleanup assertions did not finish, so this run is not claimed as completed no-witness integration.

Native peers 40156/36860, GUI child 40820 and primary 33632 exited. Independent post-run exclusive opens succeeded for the received file, fixture-held source, operational log and retained semantic copy. No running process or original native custody was reused.

## RCA: test summary writer, not transfer/runtime behavior

First failing edge: `FrontierDiagnosticPrimaryProbe.ps1`, Closed stage, attempted `Write-FileTransferEvidenceAtomicJson`. That command is loaded in the parent harness but not the standalone primary worker. The guarded probe recorded the missing-command error; after the original analyzer finished, the test worker surfaced it and exited 1. The original report verdict was not replaced or rewritten.

The Capture stage had completed, and the original custody disposal call preceded the failed summary write. However, final assertions following that write were not reached, and `diagnostic-primary-observation.json` was not created. Do not manufacture that missing observation from artifact paths.

Fix: write this non-authoritative test summary with `File.WriteAllText`, as the base test worker does for its own observations. No production helper/import/API change is needed. Added seven tests executing the real Closed probe without parent helpers: valid cleanup, open pin, retained payload text, supported primary failure, unexpected resolver call, unexpected resolution, and unexpected witness attempt. All seven pass.

The failed run and its terminal receipt remain immutable. The current harness was rebuilt for the new regression tests, but the fixed probe has **not** run in another original-pair integration.

## Next bounded step

Approve one fresh rerun of `OriginalApps_CarrySmallFrontierReceiptIntoPrimary` with the corrected probe and existing strict preflights. It must complete the observation export, original no-witness/lifetime checks and parent cleanup while preserving the actual primary verdict and stopping before route-cycle acceptance.

Do not replay the old artifacts as native custody, invoke another analyzer on them and call it the original primary, relax the 1–64 witness rule, or expand to live-256/full matrix. No commit or push was performed.

Evidence, logs, TRX results and the preservation seal are under `artifacts/native-diagnostic-custody-implementation-20260928/`.

