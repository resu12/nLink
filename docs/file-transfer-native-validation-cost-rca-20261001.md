# Native validation cost and wait semantics review

2026-10-01. Native file-channel checks, app registration and process/event
polling account for much of the sampled owned-cursor validation cost. Packet
word/tail comparisons are a small portion. A standalone experiment shows
direct Win32 zero-time waits can be faster, but are not interchangeable with
managed waits: pending thread interruption is rejected by the managed API and
ignored by the direct native call. No production wait replacement or budget
increase is implemented in this checkpoint.

This follows the
[parser rejection RCA](file-transfer-native-parser-rejection-rca-fix-20261001.md).
The first failed gate remains `owned_cursor_parse`: the local clock exceeds
5,000 ms before native content selection starts. Production acquisition,
content-family completeness and reported-candidate binding remain unproven.

## Component measurements

The added hooks compile only under `NLINK_TEST_NATIVE_CONTENT_ACQUISITION`.
They observe original calls and return their original results. Short-circuit
order, validation cadence and all normal branches are preserved. Sampling is
every 128th row and includes tracing overhead; nested measurements overlap
their enclosing measurements. These are diagnostic intervals, not a live
throughput benchmark or a prediction of GUI performance.

The first profile separates the scope components, retained files and final
app checks. It rejects at 5,001 ms after starting 102,294 rows, with 5,007 ms
outer parse time and 7,065 ms cumulative registration work. Starting every
row does not satisfy the required final boundary and completed-processing
checks. `Processed=false` and `AdapterStarted=false` remain decisive.

The second profile separates file-channel packet work. It also rejects at
5,001 ms, with 5,008 ms outer parse time, 7,536 ms cumulative registration
work and 94,138 rows started. Its same-level scope samples are:

| Original scope component | Sampled ticks | Samples |
| --- | ---: | ---: |
| File channels | 87,073 | 940 |
| Parent process and close events | 57,244 | 940 |
| App registration | 56,215 | 940 |
| Ledger origin | 24,449 | 940 |
| GUI registration | 5,254 | 940 |
| Parent registration | 290 | 940 |

Within the enclosing 87,073 file-channel ticks, cancellation/header checks
account for 56,295 ticks. Packet word reads, comparisons and tails total only
7,749 ticks, about 8.9% of that enclosing interval. Submission identity checks
add 2,244 ticks. Other caller state/exit checks and instrumentation contribute
to the remaining interval. The cancellation/header interval is not itself a
measurement of cancellation polling alone.

These results rule out packet comparison as the dominant sampled component.
They support investigating the managed zero-time polling overhead, not
removing fresh cancellation, process, state or file checks. Variations in row
counts do not establish a runtime improvement.

## Isolated wait experiment

[NativeZeroWaitObservation](../tests/nLink.SmokeTests.Core/Support/NativeZeroWaitObservation.cs)
uses its own unnamed manual-reset events and helper threads. It never receives
an original owner, app/process lease, file, custody input or verdict. Each mode
performs 20,000 polls, with three trials for each event state. Both APIs return
the expected event state on every poll.

For an unsignaled event, median managed/native elapsed times are approximately
55.1/5.0 ms on PowerShell 5.1 and 40.6/4.8 ms on PowerShell 7. The experiment
does not measure genuine owner processing, original process handles, GUI
scheduling or concurrent cancellation. It is not production acceptance or a
guaranteed speedup.

The pending-interruption probe deliberately interrupts a helper thread before
the tested poll. Both hosts report `ThreadInterruptedException` from
`WaitOne(0)`, but `native_258` from `WaitForSingleObject`, meaning the native
call returns timeout without rejecting the pending interruption. Changing the
API would therefore alter rejection behavior. Existing catch paths would no
longer receive the same exception. The faster isolated result is insufficient
to approve that semantic change.

The initial PowerShell 5.1 helper compilation omitted its System.dll reference.
That test-fixture error is repaired and its host-specific rerun passes. It is
preserved as a development failure, not classified as an application failure.

## Verification and next contract review

Focused verification covers both PowerShell loaders, normal linked Core
packet comparisons, every mapped byte for small bodies, word/tail and retained
packet mutations, maximum-size packet checks, revoked/closed views, forced
local-clock and cancellation rejection, and small original native selection.
The [receipt](../artifacts/native-validation-components-20261001/verification.json)
pins the results, measurements, current slice and preserved historical evidence.

The next step is a narrowly scoped polling-contract review. It must decide how
to preserve managed thread interruption, native errors, synchronization-context
behavior, safe-handle lifetime and original cancellation/process checks before
any replacement is implemented. This experiment does not cover all of those
requirements. If equivalent polling cannot be obtained, review the parser's
time-budget policy separately; do not silently broaden it or consolidate
validation points to force the scale gate through.

No new normal processing behavior, route authority or acceptance rule is
introduced in this slice. Linked Core and the test assembly were rebuilt and
repinned; binary identity with the previous checkpoint is not claimed.
The previous header implementation, accumulated runtime changes and historical
verdicts are preserved. There was no portable rebuild, live GUI/NKN run, commit
or push.
