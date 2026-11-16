# Native diagnostic custody: fresh verification passed

Date: 2026-09-28  
Branch: `v0.7.0`; HEAD: `a653d4063070235c71fe211480aeb37863d9b2a4`  
Result: **bounded diagnostic-custody integration passed; live route-cycle acceptance remains failed.**

## Scope and preflight

The user approved one fresh 168 KiB run of `OriginalApps_CarrySmallFrontierReceiptIntoPrimary` after the test-summary writer fix.

All 1,345 previous pins and 478 dirty-file entries matched. The Debug test project was rebuilt with unchanged project references and invocation-only `-p:UsedAvaloniaProducts=`, retaining the previous sandbox telemetry workaround. Build: zero errors, 338 warnings. The rebuilt harness and all pinned code/binary hashes are unchanged; harness SHA-256 `60f63df8ca7d3a4bf9d0588d6c67866497846f7ebc80c75c806f59a492c55670`.

The existing 21 focused passing cases remain valid on unchanged code; unrelated test lanes were not rerun. Exactly one escalated original-pair test ran this turn: **1/1 passed**, approximately 53 seconds. No implementation changes, live-256, full matrix, commit or push.

## Verified original pipeline

Fresh run directory: `artifacts/native-diagnostic-custody-implementation-20260928/pwsh-frontier-schedule-985400656c1f4ae1bad8739793a76e25`.

- Both native peers and the saved destination match 172,032 bytes and SHA-256 `67331c29dda482392a6413e0b35c3a7fa7692b5db1bfd1496da5575752f49a0b`.
- The unchanged schedule reached `PublishedBeforeOriginalDeadline`.
- Original GUI copy/persist/prepare/complete, wrapper relay, parent adoption and primary native capture passed.
- Original primary: one launch, one native source read, three evaluator calls; 1,235 events/origins in 20 bounded batches. Parent hash and line-count passes each remain two. The before/after semantic input hash is unchanged.
- Both snapshot views are authoritative, stream-complete and evidence-pass; full/compact parity passes. The original parent analysis receipt is `completed`, exit code zero.
- The fixed probe exported its observation and completed its assertions: 11 native pins, zero open after cleanup, payload text cleared, no selected proof-chain failure, zero resolver calls, no resolution or witness attempt, and `LiveProofEligible=false`.
- The harness stopped explicitly before route-cycle acceptance.

Original observation: `c3c52f6ead634047b573afbe6084aa24`; writer `p50344r75a4faf280ec40e0`; 6,537-byte diagnostic payload; SHA-256 `606ee2844d342e927bc97a4b65273bd37d02d79d4c0101cae0089219eb9dc059`.

The exported completion and marker origins match operational log lines 1437 and 1641. Completion sequence 656 has raw UTF-8 SHA-256 `ad44a4c1fc75ddb30729325d0e35cafb13bf502efcb629ffc94b737a1254a742`, matching the marker. The 1,642-line operational log hashes to `5373bec7e534ace88c0f12b5b75f3d51b0c72eec0eaf12d9b117816ddb84c4ba`.

Native peers 42680/50344, GUI child 57948 and primary 10112 exited. Independent exclusive opens succeeded for the received file, fixture-held source, operational log and retained semantic copy.

## Why the original acceptance still fails

The original primary result is `FAIL_PROTOCOL_OR_INTEGRITY`, gate `fail`, not the prior run's `INCONCLUSIVE`. It was preserved without override. Hard-failure count is zero and the selected proof-chain failure is `(none)`; no frontier witness is eligible.

Read-only inspection identifies existing broader acceptance gates:

- `Read-FileTransferCompactGuiSummary` (AnalyzerOrchestration.ps1:2115) returns null because this offline fixture has no `filetransfer-tuna-gui-summary.json`. The original integrity flag therefore remains false (line 3059). Terminal completion only sets completion state, not file integrity (line 3115). Both the previous and fresh runs record `gui_completed=1`, `gui_integrity_ok=0`.
- The original `RegularActivationCycle` proof remains failed: the small profile has only the first Tuna/fallback pair, not a second coordinator-published Tuna route. The generic classification branch at line 6184 treats missing GUI integrity or failed live-route proof as `FAIL_PROTOCOL_OR_INTEGRITY`.

Thus passing this test verifies diagnostic custody and faithful preservation of the primary result; it does **not** accept the live-256 contract or establish a data-corruption defect. Independent small-file SHA success must not be injected into a missing canonical GUI summary or used to waive the second Tuna route.

The primary-owned 1–64 witness rule remains unchanged. This run verifies the honest no-eligible-failure branch, not a failure-bearing witness publication. No failure was manufactured and no disposed native custody was reconstructed.

## Next step

The approved verification milestone is complete. Review the remaining canonical live-256 entry criteria and the required original GUI integrity/second-Tuna evidence before requesting another transfer. Do not add more small positive captures without a newly identified gap, change acceptance merely to make this fixture pass broader gates, or infer authorization for a live-256/full-matrix run.

The prior failed run, its receipt and its RCA remain untouched. New evidence and the preservation seal are under `artifacts/native-diagnostic-custody-verification-20260928/`.

