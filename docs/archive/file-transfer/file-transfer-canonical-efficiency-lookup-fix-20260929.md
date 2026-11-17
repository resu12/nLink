# Canonical efficiency-report lookup fix

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus preserved worktree.

Status: **bounded lookup implemented and verified; saved-capture efficiency
reporting now completes. The original native test has not been rerun.**

## Surgical change

`Get-FileTransferEfficiencyFrontierProgressClosures` in
`tools/FileTransferOps/AnalyzerOrchestration.ps1` now indexes receiver-state
publications by their parsed contiguous frontier once per window. Each sparse
commit searches its frontier bucket instead of the entire event window.

The original sequence comparison, event name, exact writer/session/transfer/
direction matching, numeric-frontier comparison, sorting and closure identity
logic are unchanged. All publication copies remain in the bucket; no conflicting
record is deduplicated. Authority-linked progress and its precedence are unchanged.
No runtime, coordinator, recent-grace acceptance or timeout change was made.

The existing compact-summary edit that does not infer GUI integrity from Completed
terminal states was preserved. It was not part of this patch.

## Regression evidence

The new `FileTransferEfficiencyPublicationLookupTests` run under both Windows
PowerShell and PowerShell 7. Their shared script covers 44 semantic cases:
sequence ordering; wrong or missing root scope; allowed absent direction; event
name casing; parsed, invalid and missing numeric fields; integer maximum;
duplicate publications and sparse records; authority-linked precedence; ambiguous
settlements; and independently ordered frontier buckets.

The same 44 cases also matched the frozen pre-change function on PowerShell 7.
That reference is retained only in this task's diagnostic artifacts.

A deterministic EventName-read counter exposed the old repeated scan before the
fix: **4,352 reads for 128 events**, exceeding the five-reads-per-event bound.
After the fix, the two scaling fixtures report:

| Input events | Unrelated events | Closures preserved | EventName reads |
| ---: | ---: | ---: | ---: |
| 128 | 0 | 64 | 448 |
| 640 | 512 | 64 | 1,984 |

An initial implementation used a PowerShell conditional expression that yielded
null for an absent bucket. The semantic controls caught it; the final version
explicitly initializes an empty array. Final semantic and scaling probes pass.

**16 .NET test cases passed, zero failed or skipped:**

- 4 new focused cases: semantic and scaling checks on both PowerShell hosts.
- 12 directly affected full/compact efficiency cases: productive frontier
  episodes, a no-progress episode, unpaired/stale/wrong-scope evidence, and
  duplicate/non-authoritative frontier rows.

No aggregate analyzer/contract suite or GUI matrix was run. Build warnings were
pre-existing warnings outside the changed files. The focused build used the
command-local `UsedAvaloniaProducts` telemetry opt-out; project settings were not
changed. A scoped `git diff --check` passed.

## Saved-capture reprofile

The diagnostic used the same 73,946-event capture, original local timing harness
and 300-second bound as the RCA. It ran after the affected tests finished. It did
not execute the original parent analyzer or evaluate a new coordinator snapshot.

| Measured stage | After fix |
| --- | ---: |
| Parse retained events | 44,845 ms |
| Build index | 9,866 ms |
| Progress closures: 16,015 events / 4,152 sparse commits | **7,375 ms** |
| Exact window metrics | 18,610 ms |
| Complete efficiency report | **38,877 ms** |
| Owned diagnostic process, including setup | **95,104 ms** |

Exit code 0; no timeout; diagnostic stderr empty. Previously the corresponding
probe reached the 300-second bound without completing efficiency reporting.
The first 500 sparse-loop iterations took 113,353 ms before and 170 ms after in
these instrumented captures. These are local diagnostic measurements, not
network throughput or a general performance guarantee.

The generated standalone efficiency summary is **diagnostic-only**. In particular,
its terminal-derived `sha_ok=1` is not a new file-hash observation and must not
replace the original compact GUI-integrity field or original failed route verdict.
No new authority/custody proof or full native pass is claimed.

## Next gate

Rebuild and rerun only
`FrontierCanonicalTailNativeTests.OriginalApps_CarryCanonicalTailIntoSamePairPrimary`
with strict preflights, one attempt and its unchanged 600-second outer timeout.
Refresh input pins for the approved analyzer change and rebuilt test DLL; do not
reuse the previous run's frozen-input receipt as though those inputs were unchanged.

The fixture must still preserve its intentional late-publication
`FAIL_PROTOCOL_OR_INTEGRITY` result, while completing original full/compact
snapshot parity, same-pair witness checks and cleanup. The successful offline
report profile is not a substitute for that native gate. No full GUI matrix,
deadline extension or acceptance relaxation is warranted.

## Preservation

Entry verification matched all 4,642 prior pins and 573 dirty entries. The expected
changes are the analyzer lookup, two new regression files, this report, task-local
artifacts and the rebuilt Core test DLL. The final audit distinguishes those
approved changes from unchanged original capture artifacts and unrelated work.
No commit, push, native/GUI/network run or deletion was performed.

Artifacts: `artifacts/canonical-efficiency-lookup-fix-20260929/`, including the
frozen reference function, before/after diagnostics, TRX results, bounded profile,
process cleanup and preservation verification.
