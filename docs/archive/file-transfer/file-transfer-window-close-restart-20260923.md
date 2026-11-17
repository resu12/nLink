# Window-close listener restart: evidence-finalization RCA and bounded fix

## Evidence boundary

Baseline: `f02f6d04e42d48311f1e9a4d212617f70d82e284` on `v0.7.0`.
Latest live scenario: `artifacts/second-transfer-failure-live-20260923-f02f6d04/live/20260923T184537Z/second-transfer-after-reactivation`.
All 120 pins in that campaign's `FINAL-VERIFICATION.json` were verified before editing production code.

The original second-transfer failure remains `file_tuna_v4_publication_missing_after_transfer`.
This fix addresses shutdown/evidence finalization, not Tuna route authority or transfer correctness.
No retained authority analysis was replayed, and the incomplete historical capture is not promoted to primary evidence.

## Observed shutdown sequence

Exact diagnostic locations in `filetransfer-gui-log-writer-evidence-slice.log` (SHA-256 `cab5da1fcc069ff2a961d6259a2e263aab707db88c3662dd0cc9ccaea5908f96`):

| Source line | Helpee writer sequence | Observation |
| --- | --- | --- |
| 59650 | 39905 | Main-window close starts disconnect at 18:51:52 UTC. |
| 59655 | 39908 | Helpee page enters close preparation. Its close flag has already been set. |
| 59733 | 39954 | Original bridge 25848 shuts down cleanly. |
| 59803 | 39996 | `start_helpee` starts attempt 2 after close preparation began. |
| 59829 | 40008 | New bridge 39420 is spawned. |
| 59833 | 40012 | Page disposal requests reset while the new bridge is starting. |
| 59889 | 40025 | Runtime disposal's disconnect hits its existing 3-second timeout. |
| 59890 | 40026 | New bridge shutdown begins. |
| 59909 | 40045 | New bridge requires forced termination. |
| 59911 | 40047 | Bridge shutdown completion is the helpee's last retained writer record. |

The helper has a valid final writer drain at source line 59882, sequence 19864.
The cleanup diagnostic reports the helpee still running after the existing shared 7-second wait and a force-stop request. The ledger reports one missing drain, for the helpee; other passing writer checks do not substitute for it.

`Program.Main` emits the final operational-log drain in its `finally`, after the desktop lifetime and synchronous view-model disposal return. `Write-FileTransferGuiLogLedgerEvidence` only issues the native writer completion when both ledger and writer integrity reason lists are empty. `writer_drain_missing` therefore blocks completion issuance. Without that original completion, the wrapper/parent cannot obtain the failed-capture object and does not dispatch primary retained analysis. This is the intended fail-closed handoff, not a missing-file fallback to repair.

## Root cause and limits

The first reproducible shutdown defect is **new helpee hosting admitted after window close**:

- `PrepareForWindowCloseAsync` latches `windowCloseDisconnectStarted`, but hosting did not check it. Post-terminal return-to-waiting callbacks could start another listener during close.
- `StartHostingAsync` awaited reset and then started hosting without rechecking close state. A restart queued before close could also cross that asynchronous boundary after close.
- Main-window close started disconnect before invoking page close preparation, allowing synchronous terminal callbacks before the page had latched close intent.

The live writer ordering proves the unwanted restart occurred after the page entered close preparation. Deterministic fake-transport tests reproduce both restart paths independently of NKN connectivity. This establishes an actionable defect on the shutdown path; it does **not** prove it is the only contributor to the final drain timeout. The capture contains no stack trace proving exactly where helpee disposal remained blocked after the last bridge record. Do not infer a transport deadlock or adjust time budgets from this evidence.

## Bounded implementation

1. Start page close preparation before the main-window end-session callback, without awaiting page completion first. Existing parallel teardown and deadlines remain.
2. Refuse `StartHosting` when disposed or when window close has begun, before changing hosting attempt/generation state.
3. Recheck the same condition after asynchronous reset and before starting the next helpee listener.

Only two app view-model files change. Normal non-closing return-to-waiting remains enabled. No coordinator decisions, transport disposal, bridge lifecycle authority, log drain semantics, capture issuance, shutdown timeout, retry cadence, screen-sharing behavior, or performance policy changes.

## Verification

Evidence directory: `artifacts/window-close-restart-20260923/tests`.

- `before-fix.trx`: 4 expected failures and 1 passing non-closing control. Failures reproduce both close-order checks, post-close restart generation, and a queued restart creating a second transport after close.
- `after-fix.trx`: all 5 focused cases pass.
- `affected-gui.trx`: all 10 selected existing regressions pass: bounded/hanging close preparation, preparation failure, normal end-session waiting UI, peer-ended/identity-preserving restart, rejected-help retry, bridge bootstrap rehosting, visible liveness failure, and close notification to the peer over fake NKN.
- `evidence-contract.trx`: all 3 selected contract tests pass. The ledger still rejects missing writer drains; both PowerShell hosts retain the original failed outcome, reject unsealed/no-drain capture fixtures, and enforce single parent-owned analysis.
- `artifacts/window-close-restart-20260923/Verify-Implementation.ps1` records the 18 passing post-fix tests, 120 preserved live pins, 11 exact shutdown diagnostic locations, and source/test hashes in `FINAL-VERIFICATION.json`.
- No new live GUI test, full matrix, package rebuild, commit, or push is part of this offline fix. Existing compiler/analyzer warnings remain; no build errors were observed.

## Next validation gate

Review/checkpoint this bounded patch, rebuild the portable package, and rerun only `second-transfer-after-reactivation` with strict preflights and no retries. The old capture remains failed and must not be manually reanalyzed.

Require no new helpee hosting attempt after close preparation, both valid final writer drains, and an authentic parent-owned retained-analysis handoff if the GUI scenario fails. If the targeted run still lacks a drain, use its new shutdown evidence to locate the next edge; do not relax issuance checks. If primary evidence becomes available, let that single analysis select exact witnesses for the unresolved second-transfer Tuna-v4 publication failure. A complete payload hash alone remains insufficient for route acceptance.
