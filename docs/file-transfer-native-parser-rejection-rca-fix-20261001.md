# Native parser clock RCA and bounded header implementation

2026-10-01. The large offline input is rejected by the parser's local clock at
5,001 ms, before native content selection starts. This directly establishes
the predicate that was still uncertain in the
[previous acquisition checkpoint](file-transfer-native-content-acquisition-implementation-20261001.md).
A bounded change to GUI request header reading passes comparison and custody
regressions, but does not clear the scale gate. Complete acquisition and
production integration remain unready.

## Exact rejection and measured cost

Test-only tracing records the first failed predicate without resetting work,
providing success tokens or changing the guard order. Forced local-clock and
native-cancellation controls distinguish `local_clock` from
`native_buffered_custody` on both PowerShell hosts. The genuine large offline
fixture reports `local_clock` at 5,001 ms; its original cumulative registration
work remains below the separate 10,000 ms ceiling.

The pre-change detailed sample assigns 362,275 ticks to source reading out of
426,627 sampled row-stage ticks, about 85%. Within source reading, native reads
account for 252,541 ticks and full boundary checks for 86,425. The sealed-file
I/O portion is only 4,596 ticks. These measurements identify native validation
as the dominant sampled cost. They do not isolate the cost of each native
validation predicate, and nested intervals must not be added to their parents.
Samples cover every 128th row, not the entire run, and include tracing overhead.
They are not a throughput benchmark or live-transfer RCA.

## Bounded implementation

[MatchesRequest](../tools/FileTransferOps/LocalGuiWriterFrontierLaunch.cs)
now reads one fresh 64-byte GUI request header on each call, replacing nine
scalar accessor reads and the separate request-ID read. It still compares the
magic, request ID, original parent and GUI incarnations, registration length,
allowed stage and every reserved field. The registration body is still read
and compared independently. No success is cached, and the snapshot is not
claimed to be atomic.

Every original validation point remains: entry and exit native checks,
before/after refill checks, file identity and extent checks, process lifetime,
original-object ownership, cancellation, chronology, complete row processing,
origin capture and all budgets. The three other modified production tool
files contain only conditionally compiled diagnostic hooks; their normal
branches retain the original checks. No route/coordinator authority, content
coverage, candidate binding or acceptance rule is added by this change.

The new comparison fixture has an inert, unregistered owner with no process
lease, file seal, custody receipt or authority verdict. It compares the new
method to the old matcher for each single-bit mutation in every header and
registration-body byte, checks restored bytes freshly, and exercises stages
0, 1, 2 and -1. It passes 1,301 comparisons per host. This verifies the static
comparison behavior and fresh repeated reads, not atomicity under concurrent
mutation.

## Verification and remaining failure

There are 36 unique passing focused regressions: two header comparison cases,
two original launch-contract cases containing 25 scenarios each, 28 buffered
reader/custody cases, two small native input-selection positives and two native
parent-death cases. The normal loader excludes the diagnostic compilation
symbol. The test assembly and Core were rebuilt. Core links the edited seal
source, so its binary hash differs from the prior checkpoint; it is repinned,
not described as byte-identical. The normal Core binary contains no diagnostic
helper reference. Existing analyzer warnings and NU1900
feed-access warnings are not new failures or a completed vulnerability audit.

The large fixture now has distinct normal and profiling modes. Normal scale
uses the production loader and `TryProcessObservedLedger`; the profiling mode
uses the same source operations and budgets plus passive tracing. Both remain
failing positive tests, not expected-success substitutes or waived gates.

Normal scale rejects with `Processed=false` after an observed 5,059 ms and
7,529 ms cumulative registration work. The traced follow-up rejects during
`owned_cursor_parse` with `stream_source_not_current`, `local_clock` at
5,001 ms, 5,008 ms outer parse time and 6,978 ms cumulative registration work.
It starts 99,078 rows before rejection. Native content selection never starts.
Differences between the sampled row counts are not sufficient evidence of a
performance improvement: earlier pre-change profiles also reached roughly
99,000 to 100,000 rows.

This generated size/custody fixture contains 102,215 generated rows, including
5,524 labelled shape markers, in a roughly 208 MB source. Those markers are
not genuine coordinator boundaries or complete security/publication histories.
The original fixture and historical live evidence remain preserved. The
[verification receipt](../artifacts/native-parser-rejection-20261001/verification.json)
pins the current code, results and prior evidence without promoting this
partial implementation.

## Next bounded work

The first missing edge remains completion of the original owned-cursor parse
within its five-second bound. Instrument the individual native validation
components at the existing call sites to distinguish mapped header/body
reads, process/event checks, file-channel checks and file identity checks.
Only optimize a measured component with fresh observations and equivalent
comparisons; do not skip or cache custody approvals, consolidate validation
points, enlarge buffers or raise budgets to force this gate through.

After the scale gate passes, the separate event-family provenance and
reported-candidate binding requirements still remain. They must be completed
under original primary custody before production integration or new live
acceptance validation.

No portable rebuild, live GUI/NKN run, commit or push was performed. Existing
runtime changes, `Recovered`, strict exact-resend acceptance, route invariants
and historical verdicts are unchanged. All 99 historical live pins are checked
by the new receipt verifier.
