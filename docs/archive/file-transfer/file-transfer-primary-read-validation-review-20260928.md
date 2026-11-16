# Native source-read validation: measurement and bounded contract review

Date: 2026-09-28. Status: measurement and contract review complete.
No production or acceptance change is implemented. The proposed validation
cadence below requires explicit approval before implementation.

## Current first failure

The latest rebuilt canonical remains
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-ad1266bbb1104b7ebf9c79f3356c7e2c`.
It returns 72,896 of 73,719 semantic lines before `resource:budget` closes
`ReadSource` at 20,001 ms. EOF, origins, evaluations, and binding are not reached.

The earlier checkpoint attributes 11.4788 seconds of read work to full
file/facts checks and 5.8714 seconds to native-pin checks, each called 147,901
times. Mapped reads account for only 0.1063 seconds. That evidence identifies
validation work, not transfer goodput or mapped I/O, as the investigation target.

## Representative component measurements

Both controls use the latest observed 1,928,535-byte facts body and 147,901
checks. They create codec-free transport data; no bytes are submitted to an
analyzer, native acceptance, or coordinator evaluation. The checkpoint hash
audit finished before either timed control ran.

| Component | Existing control | Comparator comparison run |
| --- | ---: | ---: |
| Full facts validation | 8.8959198 s | 8.6728463 s |
| Guarded body comparison | 7.6862576 s | 7.7301273 s |
| Header alone | 0.0886837 s | 0.0933993 s |
| Fixed file offer, without facts | 1.2510751 s | 1.2094864 s |
| Existing MSVCRT body loop | 7.7058571 s | 7.6509293 s |
| Alternative UCRT body loop | not run | 7.6370610 s |

The isolated loops hold the same mapping/managed-array pins around their
measurement loops. They are not the full guarded production method. Components
are measured sequentially, not as a partition of one invocation. Elapsed time
includes scheduling; these are not CPU-only timings or a statistical guarantee.

The alternative is only 0.1813% faster in this comparison. That is insufficient
evidence for replacing the existing comparator. It corroborates the earlier
rejected UCRT experiment in the origin-validation review at the newer read
workload; it is not a new algorithmic improvement. No production import changed.

Microsoft documents the same byte-count/zero-for-equality semantics for
[`memcmp`](https://learn.microsoft.com/en-us/cpp/c-runtime-library/reference/memcmp-wmemcmp?view=msvc-170),
and UCRT availability on Windows 10 and later in its
[deployment documentation](https://learn.microsoft.com/en-us/cpp/windows/universal-crt-deployment?view=msvc-170).
These establish why the isolated comparison was reasonable, not evidence that
one implementation must be faster or that older host support may be changed.

The current workload compares 285,232,255,035 logical body bytes, before counting
both input buffers, headers, pins, or other work. Replacing one exact comparator
with another has not materially reduced that repeated work. This does not
prove every possible same-cadence optimization is exhausted; it rules out the
specific candidate measured here.

The temporary benchmark addition was removed. The existing control is restored
byte-for-byte to SHA-256
`3aac393648b9664acb9403aa2b05e6d939367c59df1b7dd4a01f3af01c2378e6`.
The candidate source and reproducible runner are retained only under
`artifacts/primary-read-validation-review-20260928/`.

## Smaller proposed contract: full validation at each line publication

Recommend reviewing a per-line contract, not a multi-line batch or a larger
budget. The current `GuiSmokeLedgerLfRead` calls the same full-currentness
callback before each fragment and again immediately before returning the
decoded line. The native `ReadSource` also performs its original entry checks.
The proposal changes only which checks run during construction of that line.

1. Keep the original native reader, single source pass, owner/inputs locks,
   reader-identity gate, ordering, exact emitted string references, all source
   lines/origins, and cumulative work clock. No new authority token, alternate
   evaluator, source reopen, prefetch batch, or cross-line success cache.
2. Retain full resource validation at open/begin, before every line is returned,
   at genuine EOF, and at source completion. Full validation still includes
   live file-offer/facts bytes and native pins, not merely their cancellation
   signals. A prior line's successful validation cannot authorize the next.
3. At the existing fragment checks, use abort-only closed/budget/base-liveness,
   scope, file-cancel and facts-cancel checks. Keep checks at line entry and
   after the closing full validation, so expiry during that final scan cannot
   publish a line. These checks cannot grant custody or publish output.
4. Hold the line private until its closing full validation and final abort-only
   checks succeed. A persistent mismatch or retirement detected there rejects
   the current line through the original close path. Earlier successfully
   returned lines are not retroactively reclassified as a completed source.
5. Preserve strict UTF-8, BOM/CRLF/final-record behavior, the existing 32,768-byte
   read buffer, 262,144-code-unit maximum line, 262,144-line cap, cumulative
   source-unit bound, and true-EOF byte/count reconciliation. Construction is
   bounded to one existing maximum-size record, not 64 records. Keep all
   original fragment-level abort opportunities, including long records.
6. If the shared line reader needs a distinct line-completion callback, make
   that optional and native-source-only. Its default must retain the original
   callback at all original positions for ledger and other consumers. Do not
   globally weaken the shared reader or change its output API.
7. Charge all added/rearranged checks and decoding to the original work clock.
   Retain the 20,000 ms limit and original cleanup. No timing exclusions or
   subtraction of measured overhead.
8. Leave the approved post-EOF origin batch contract, payload binding,
   three-evaluation/one-native-read requirement, parent proof expectations,
   coordinator authority, and live route-cycle acceptance unchanged.

### Material tradeoff requiring approval

This is a validation-cadence change, not a semantics-neutral implementation
optimization. A facts or pin mutation that occurs and is fully restored while
one record is being constructed may be observed less often than under the
current full checks at every fragment. The closing full check rejects a
detectable persistent mismatch before that line is exposed; it does not prove
that no transient mutation occurred earlier.

Cheap cancellation/liveness checks remain at the original fragment cadence.
A complete full resource check remains mandatory for every returned line.
No sampling of records or origins, multi-line publication, cached full verdict,
or acceptance from an earlier success is proposed.

The record limit bounds the reduced-observation interval by one record's
construction, not by a fixed wall-clock duration. The existing active-work
budget and cancellation signals still apply. The proposal must not be presented
as continuous facts/pin observation.

Approval must explicitly accept this transient-mutation detection tradeoff.
The existing origin-batch approval does not automatically authorize it for
source reading. Until approved, current code and validation cadence stay intact.

## Verification plan if approved

- Add focused controls on both PowerShell hosts for normal lines, empty input,
  final unterminated semantic record, CRLF/BOM/UTF-8 boundaries, maximum-length
  records, malformed/oversized data, and exact source counts/references.
- Prove full validation before each returned line and EOF, with fragment
  abort checks in original order. Use original checks once, not extra
  currentness evaluations for diagnostics.
- Inject persistent facts/pin changes before and during a record, cancellation,
  copied reader, owner disposal, scope changes, and budget expiry at entry,
  fragments, closing validation, and after it. A failed current line must not
  escape; no fake EOF or downstream evaluation may replace rejection.
- Include an explicit mutate-and-restore control documenting the newly approved
  observation limit, without granting route or integrity authority.
- Retain default shared-reader behavior and focused original scalar/source
  compatibility tests. Do not rerun unrelated aggregate lanes.
- Rebuild and run one targeted offline canonical. Require genuine EOF, all
  original origins, then unchanged positive binding/cleanup and three original
  evaluation assertions. A newly exposed proof edge gets its own RCA.

Expected savings are not yet measured. No live-256 test is justified by this
component result or proposal. A second coordinator-published `file_tuna_v4`
remains required; payload SHA and fallback completion are insufficient.

## Scope and preservation

Both component controls exit successfully. No new `dotnet test`, rebuild,
canonical replay, live GUI/NKN run, portable build, full matrix, commit, or push
was performed. Existing runtime, fixture, test, and assembly bytes are unchanged
after restoring the exploratory benchmark edit. This is a reviewed proposal,
not a completed performance fix.

All 8,357 starting pins matched before work. Starting seal:
`artifacts/primary-binding-classification-20260928/FINAL-VERIFICATION.json`,
SHA-256 `307232c2593ab204b6078cf30f935228dddd1c688a41b964b6c4e0bdccb3fd20`.
The 439 starting dirty entries remain untouched; this report is the only new
non-ignored workspace entry. HEAD remains
`a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`.

Component observations, candidate source/runner, final preservation manifest,
and hash audit are under `artifacts/primary-read-validation-review-20260928/`.

