# Canonical native capture: offline analyzer RCA and bounded fix plan

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus preserved worktree.

Status: **the late-publication failure is expected by this diagnostic fixture;
the outstanding execution blocker is quadratic efficiency-report work.**
This review changes neither runtime code nor acceptance. The original native
test remains failed at its 600-second timeout, not accepted as a route-cycle pass.

This supersedes the *interpretation and next-step ordering* in
`file-transfer-canonical-prefix-setup-native-rca-20260929.md`; its capture,
timestamps, timeout and preservation facts remain intact.

## Evidence and method

The only source capture used is
`artifacts/canonical-tail-native-characterization-20260929/pwsh-646a050ab737427aa2be805713604e79/`.
The retained log has 73,946 events, SHA-256
`01801c8826d7621cc0cf6e3441146ad5c4e1b3df2f13e301515e90e13688327c`.

A 947-event slice, retaining original lines 73000–73946, was parsed with the
repository parser. Both the legacy and indexed recent-grace reducers reproduced
one unresolved candidate at line 73754. Session-local instrumentation recorded
the closure predicate that returned null. No source record, timestamp, predicate
or authority result was changed. This bounded diagnostic did not evaluate a new
coordinator snapshot or create native-writer custody.

Separate hidden, owned PowerShell children profiled only efficiency reporting
from the saved log, with a 300-second diagnostic bound. They did not run the
native harness, NKN, GUI scenarios or the original parent analyzer. Their output
directory is separate from the original capture and is diagnostic-only.

## 1. Semantic rejection is intentional, not a missing recheck

The exact recheck, recent arm and stall arm each match uniquely. The missing
closure edge is **receiver publication before the original stall deadline**,
not absence of the recheck record.

Receiver writer: `p39532r6db1cfefe0b24a92`.
Original stall deadline: `2026-09-29T06:26:55.2518355Z`.

| Retained line / writer sequence | Evidence | Relative to deadline |
| --- | --- | ---: |
| 73762 / 42484 | Exact recent-grace recheck, `rechecked / stall_grace_armed` | −747.4111 ms |
| 73775 / 42496 | Sparse commit advances frontier 12477 → 12480 | −489.9410 ms |
| 73777 / 42498 | Old stall callback rejected, `stale / frontier_scope_changed` | −0.7544 ms |
| 73802 / 42503 | Receiver state publishes frontier 12480 | **+122.9655 ms** |
| 73810 / 42512 | Next sparse commit advances frontier 12480 → 12484 | +127.5984 ms |

`Get-FileTransferRecentProgressGraceStallProgressClosure` finds no timely
publication for the first advance; the next write is already outside the wake
interval, so it returns null. Legacy and indexed diagnostics agree. The stale
callback is not successful closure and remains diagnostic-only.

Crucially, `Support/FrontierCanonicalTailProof.ps1` explicitly requires
`FAIL_PROTOCOL_OR_INTEGRITY`, `gate_status=fail`, and the selected failure
`filetransfer_fallback_frontier_recent_progress_grace_recheck_missing`.
`FrontierCanonicalTailPrimaryProbe.ps1` also rejects any promotion of that
original failed verdict. The fixture's purpose is to carry the original actual
pair and late-publication witness through diagnostic resolution and cleanup,
not to pass route acceptance. Its observation bound is not the runtime's
750 ms publication deadline.

Therefore the prior plan to address this semantic rejection before retrying the
fixture was too broad. **Do not fix runtime timing or relax the analyzer to erase
this deliberately retained failure.** The missing final same-pair observation
still has to be established by a completed original-parent run.

## 2. `sha_ok=0` is not a failed file-hash comparison

The compact `transfer-terminal-summary.txt` writes `sha_ok` from GUI summary
`integrityOk`, defaulting to false. This headless capture has no
`filetransfer-tuna-gui-summary.json`. The same code can infer both Completed
terminal states from log events but explicitly does not turn terminal status
into independent GUI hash/size evidence.

This explains Completed / `sha_ok=0` alongside the independently verified source
and destination SHA-256 from the previous cleanup. It does not authorize
fabricating GUI integrity evidence or promoting route acceptance. No change to
this reporting behavior is proposed here.

## 3. Execution RCA: repeated whole-window publication search

The first successful timing probe parsed 73,946 events in 44,825 ms and indexed
them in 9,680 ms. Within efficiency reporting, semantic selection took 1,132 ms
and indexed metrics took 3,908 ms. It entered
`Get-FileTransferEfficiencyExactWindowMetrics` and did not return before the
owned diagnostic child reached its 300-second bound.

The detailed probe isolated the pending path:

1. `Get-FileTransferEfficiencyExactWindowMetrics`, final per-window episode loop.
2. `Get-FileTransferEfficiencyFrontierEpisodeMetrics`.
3. `Get-FileTransferEfficiencyFrontierProgressClosures`, sparse-commit loop.

The first observed window has **16,015 events and 4,152 sparse commits**.
For every eligible sparse commit, the current implementation pipes all
`WindowEvents` through the receiver-publication predicate, then sorts the matches.
The nested-loop upper envelope for this window alone is 66,494,280 candidate
visits, before additional windows and closure-identity scans. This count is
derived from measured input sizes and code structure, not a claim that the
bounded probe completed all those visits.

The detailed trace reached that sparse-commit loop while all preceding window
loops completed. Its commit-index markers advanced from 1 at 1,295 ms to 501 at
114,648 ms of the closure call: **113,353 ms for the first 500 loop iterations**.
This localizes the delay to report correlation rather than
parsing, index construction, network readiness, file transfer or a missing live
callback. The source contains additional scans, but they are not independent
justification for unrelated optimization in this patch.

## Bounded implementation plan

1. In `Get-FileTransferEfficiencyFrontierProgressClosures`, build a local lookup
   of receiver-state publications keyed by parsed contiguous frontier once per
   window. Select the matching bucket for each sparse commit, then retain the
   existing sequence, exact window-root, numeric-frontier and sort predicates.
   Keep every duplicate/conflicting record; do not deduplicate or infer authority.
   Initially change only this demonstrated whole-window search.
2. Add deterministic output-parity controls for timely/later sequence, wrong
   writer/session/transfer/direction, missing and invalid numeric fields,
   duplicate publications, duplicate sparse records, and authority-linked versus
   natural progress. Add a candidate-visit/scaling regression instead of relying
   solely on a wall-clock threshold. Preserve existing closure return shape.
3. Run focused closure tests, then directly affected productive-frontier,
   no-progress and wrong-scope efficiency tests. Do not rerun unrelated aggregate
   analyzer/contract suites. Reprofile this saved capture with the same bounded
   diagnostic, retaining the late-publication failure as expected.
4. Only after the measured execution problem is resolved, rebuild and retry the
   **single original canonical native fixture** with its unchanged 600-second
   outer deadline, original input/custody contract, one attempt and strict
   preflights. Verify original full/compact snapshot parity, same-pair witness,
   unchanged failed route verdict and cleanup. Do not run the full GUI matrix.

No deadline increase, GUI integrity substitution, throughput tuning, coordinator
authority change or second authoritative analysis of the saved capture is part
of this plan. A new first execution edge must be recorded rather than bypassed.

## Artifacts and preservation

Reproduction scripts, exact timelines, predicate diagnostics and profiling logs
are under `artifacts/canonical-analyzer-offline-rca-20260929/`.
The initial profiling launch failed before parsing because its instrumentation
read the wrong AST shape; that error and its process receipt are retained. The
corrected probes use a parsed function-body AST and session-local wrappers only.

Entry verification matched all 4,622 prior pins and 572 dirty entries. No
production or test source has been changed in this review, no .NET tests or new
native/GUI/network runs were made, and no commit or push was performed. The final
verification records preservation of the original capture and the new RCA files.
