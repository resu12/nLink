# Canonical witness native profile: RCA and bounded fix plan

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus preserved worktree.

Status: one fresh offline native 256 MiB attempt completed and failed closed.
The new profile identifies **input-row materialization/parsing inside the
PowerShell witness callback** as the first operation that exhausts the remaining
cumulative work budget. No implementation or acceptance change was made here.

## Execution

Entry verification matched 5,319 pinned files and 595 dirty entries. The Core
test harness rebuilt with zero warnings/errors; its DLL remained
`c15aa47dff97774e83dbd5eb1283a38afc6b71181178f7a0065997359fee6e17`.
Frozen run inputs contain 5,324 pins and the same dirty set, with SHA256
`df62ace37a7cfb2b3d364fccd6772b5bc9fa4346865740ac400de4f0215de614`.

Both strict preflights ran outside the restricted sandbox and passed, including
the latest 13 distinct passing witness-profile/composition controls. Previously
verified controls were checked from their pinned results, not rerun. Process
isolation, debug policy, clock and disk checks passed.

Exactly one
`FrontierCanonicalTailNativeTests.OriginalApps_CarryCanonicalTailIntoSamePairPrimary`
ran and failed after **351.0954939 seconds**. Neither its 600-second outer timeout
nor the analyzer's 1,800-second limit expired. No retry, live NKN connection,
portable GUI rerun, full matrix or saved-capture evaluation occurred.

Fixture:
`artifacts/canonical-tail-native-characterization-20260929/pwsh-a0c33b9ef5ac40f99a50934eed473c2c/`.

## Transfer, original evaluation and cleanup

Both original peer processes completed 268,435,456 bytes on
`post_tuna_fallback_v6`, with the same expected SHA256:
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
The late-publication episode closed within its original observation bound,
with three physical copies and unchanged root. The final recheck was
260.9560 ms before its unchanged deadline; three completed retained-tail reads
took 627.3912 ms total. This is another passing scheduling observation, not proof
that the earlier intermittent scheduling concern is solved.

The original analyzer used 73,955 full lines and 1,583 compact lines, with
three evaluations and one native read. Its original input hash/line count stayed
unchanged. Full and compact snapshots were SnapshotV1Authoritative,
stream-complete, evidence-pass, with 1,507 boundaries each and parity pass.

Witness count remains zero, same-pair join and diagnostic output commit false.
The original selected failure remains
`filetransfer_fallback_frontier_recent_progress_grace_recheck_missing`,
with verdict FAIL_PROTOCOL_OR_INTEGRITY and gate fail. Retained analysis exited 1
with completed proof artifacts, not a timeout.

All ten original report hashes matched before/after and still match disk.
Native pins closed to zero, text was cleared and the evaluation retired.
Independent out-of-sandbox cleanup found no overlapping/native test process;
peer PIDs 57104/37024 and analyzer PID 49724 exited. Both payload files were
exclusively readable, correctly sized and SHA-correct.

## Measured first failing edge

The unchanged work limit is 20,000 ms; visit limit is 2,097,152.
The one-shot profile recorded one resolution call, custody state 3,
stage `selection-validation`, reason `rejected`, and the original callback
omission `frontier_evaluation_budget`.

| Interval / counter | Observed result |
| --- | ---: |
| Cumulative work already spent at resolver entry | 14,335.3653 ms |
| Availability/currentness check | 2.2126 ms |
| C# original-state capture | 47.8592 ms |
| Entire selection callback | 5,617.7873 ms |
| Callback entry to first checkpoint | 10.4033 ms |
| Initial failure/view snapshot | 3.0171 ms |
| Input-row phase until terminal failure sample | 5,603.9566 ms |
| Terminal sample to C# selection-validation | 0.4103 ms |
| Final cumulative work | 20,003.2249 ms |
| Final cumulative visits | 1,842,870 |
| Visits remaining below cap | 254,282 |
| Materialized input rows | 6,124 / 73,955 (8.28%) |
| Input-phase visits, including rejecting visit | 6,125 |
| Owned rows / selected witnesses | 0 / 0 |

Callback phases 0 and 1 were entered. Terminal slot 9 identifies phase 1,
terminal=true, failed=true. Phases 2–8 and C# witness-capture/final-currentness/
accepted stages were not reached. The source loop visits before parsing each
row, which explains one more visit than successfully materialized rows.

The callback catches the original budget exception and returns null; C# records
selection-validation then rejects null before its subsequent state-currentness
walk. This is not a state-capture failure, a visit-cap failure, a later witness
validation rejection or a route correctness result.

Most cumulative work was already spent before this optional resolver. Within
the callback, however, nearly all measured time was consumed by the unfinished
input-row phase. That phase performs original row/path identity checks, text and
field bounds, structured-field parsing, scope/data-session parsing and row
construction. The phase timing does **not** isolate the cost of each parser,
regex, token calculation or object allocation, so no single sub-operation is
claimed as the measured cause. Profiling adds bounded overhead; the roughly
5.60-second span is inclusive of the original failure unwind to its sample.

## Bounded fix plan

1. Add deterministic differential fixtures for the exact input-row projection,
   using the current PowerShell implementation as the oracle, not saved native
   evidence. Cover duplicate/case-variant keys, first-value retention, writer
   duplicate flags, prefixes/whitespace, malformed/redacted/projected scope,
   data-session conflicts, text/field capacities and non-ASCII values.
2. Implement a pure bounded compiled projection for the input-row phase only,
   preserving every original identity/path/bounds check, failure ordering and
   visit charge. Keep one original visit per attempted row and the same failure
   event identity/duplicate detection. It must not select ownership, create
   custody, evaluate authority or skip/filter input rows.
3. Keep snapshot/currentness, receipt matching, owned-row scans, later witness
   selection, native lifetime and coordinator authority unchanged. Do not
   increase any work/visit limit or spend budget outside the original accounting.
4. Run only directly affected parser/scope/projection, witness diagnostic and
   custody controls on both PowerShell hosts, then one relevant contract lane.
   Compare the optimized phase against the oracle on deterministic bounded
   fixtures. This is a diagnostic-selection optimization, not transport tuning.
5. Rebuild/freeze inputs and use strict out-of-sandbox preflights for one fresh
   targeted native attempt. It must prove this first edge advanced; later
   budget or proof failures must be reported separately, not bypassed.

A tempting shortcut is unsafe: `Index.AllEvents[*].Fields` is produced by
`tools/FileTransferSoak/LogParsing.ps1`, which overwrites repeated keys. The
witness parser in `tools/GuiSmokeStructuredFields.ps1` retains the first value
and sets duplicate flags. Reusing the former as equivalent evidence would erase
required ambiguity checks. This plan does not authorize that substitution.

The new evidence establishes only this first failing phase. Faster row
projection does not establish that all later scans, revalidation and output
operations fit the remaining work/visit limits. No such pass is claimed.

## Preservation and artifacts

Run inputs, preflight records, TRX, profile arithmetic, original timing lines,
cleanup and final verification are under
`artifacts/canonical-witness-work-native-20260929/`.
The raw timing lines preserve full-width integer clock values; arithmetic was
performed before JSON conversion. Production/test sources and acceptance
contracts were not edited. No commit, push, cleanup deletion or automatic retry
was performed. The full live GUI matrix remains gated.
