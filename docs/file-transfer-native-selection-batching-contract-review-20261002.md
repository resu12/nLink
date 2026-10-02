# Native selection batching contract review

2026-10-02. Do not implement batch-boundary resource validation as an
equivalent optimization. It can accept an input that an existing removed
check would reject. Keeping the scan private and validating before export
does not preserve that rejection behavior. This review changes documentation
only; validation cadence, budgets, acceptance and route authority stay intact.

The next bounded step is a selection-only component profile that preserves
every original resource call. It should distinguish base ownership, request
mapping, file/facts channels and retained file pins before proposing an
optimization within a check. No successful scale acquisition is claimed.

## Current guarantees

The [input-selection adapter](../tests/nLink.SmokeTests.Core/Support/NativeContentAcquisition.cs)
holds the original worker and input locks throughout selection. It checks the
complete event/raw-line index before invoking the primary selector and again
after building its witnesses. Each valid full event row makes four fresh resource
checks through its record visit and path-unit charge. Compact rows, cursor
access and origin lookups have their own original checks and accounting.

The following obligations must not be merged into one cached currentness flag.

| Obligation | Current check | Requirement for an equivalent change |
| --- | --- | --- |
| Cursor and custody | Active cursor, original evaluation, taken state, completed source | Preserve the original checks and one-shot selection slot |
| Work and capacity | Inherited clock, producer record/code-unit admission, native evaluation visits | Preserve charging, thresholds and rejection order |
| Original membership | Event references, exact note-property types, raw-line references, source path, full/compact references | Inspect every original row in both scans; retain exact physical origins |
| Native resources | Fresh base, scope, file/facts and pin predicates | Preserve each original invocation and its short-circuit order |
| Selector and output | First complete scan, 1–64 ordered witnesses including the selected event, second complete scan, ended cursor | Do not invoke selection on an incomplete first scan or export partial witnesses |

[ResourceCurrentWithin](../tools/FileTransferOps/LocalFrontierPrimaryWorker.cs)
checks worker close, the cumulative allowance, parent/request lifetime, the
live request mapping, file/facts channels and retained file handles. The
[scope predicate](../tools/FileTransferOps/LocalFrontierPrimaryTransport.cs)
compares the live mapped header and body with its retained originals. The
[file channel](../tools/FileTransferOps/LocalFrontierParentFileChannel.cs) and
[facts channel](../tools/FileTransferOps/LocalFrontierParentFactsChannel.cs)
also compare live mapped contents and check cancellation and protocol state.
The [pin predicate](../tools/FileTransferOps/LocalFrontierParentCommitPins.cs)
rechecks every retained handle's file identity and size through
[Matches](../tools/FileTransferOps/LocalGuiWriterParentFiles.cs).

These are observations at specific calls, not a guarantee of continuous
immutability between calls. Nevertheless, removing a call changes which
invalid states can be observed. The local locks do not lock peer mappings or
external event/process state. Registered waits call `Close`, which takes the
input lock already held by selection; a queued callback cannot replace fresh
polling inside that locked interval. There is no reviewed native mutation
epoch that records every change and restoration across all these resources.

## Why private batching is not equivalent

In an otherwise valid, in-budget invocation, consider a schedule in which one
mapped request byte changes after a proposed batch entry check, remains
changed across an existing resource check that the
proposal removes, and is restored before the batch exit check. The current
scope comparison returns false at that existing call, and selection returns
unproven without witnesses. The proposed boundary checks see the original
bytes at both ends and can proceed to selection and successful input export.
This is a source-level counterexample, not an observed application incident
or a newly executed concurrency test.

Keeping all row/reference checks and withholding output until the final
boundary does not catch that request-mapping change. Checking a batch of one
row only at its edges is also insufficient if it removes any of that row's
four existing check points. A smaller batch bounds the observation gap; it
does not eliminate it. An untrusted or incomplete mutation counter, cached
hash, previous successful predicate or asynchronous callback is not a
substitute for the removed checks.

Persistent cancellation can still be rejected at a later boundary, but the
private work and visit counts before rejection may differ. Removing managed
wait calls also changes the opportunities to observe interruption or a wait
context callback. The earlier
[polling review](file-transfer-native-polling-contract-review-20261001.md)
already rules out its two native-poll replacements; it does not validate
fewer managed calls. No additional polling experiment is needed to reject
this equivalence claim.

Batching could only proceed under an explicitly revised diagnostic contract
that permits those differences, or a separately proven original mutation
protocol covering every affected resource. Neither is approved or implemented
here. It must not be described as retaining the current rejection contract.

## Allowance dependencies

The fresh [scan profile](file-transfer-native-selection-scan-profile-rca-20261002.md)
inherits 18.2497104 seconds and completes 3,633 of 102,218 rows before the
unchanged 20-second cumulative guard rejects. Full resource checks account
for 99.5224% of the measured initial scan. Its approximately 49.3-second first
scan and 98.5-second two-scan projections are diagnostic-prefix estimates,
not bounds or an unprofiled completion measurement.

Raising only the adapter's allowance would not provide an isolated new
budget. `Visit()` also calls `inputs.VisitEvaluation()` on the same clock.
With an evaluation present, that method independently enforces
`EvaluationMilliseconds=60000`, while the adapter and its resource predicate
use `ProducerMilliseconds=20000`. Subsequent `Owns` and physical-origin
validation use evaluation currentness and visit charging too. The constants
and consumers are in
[LocalNativeInputCapture](../tools/FileTransferOps/LocalNativeInputCapture.cs)
and [LocalFrontierPrimaryEvaluations](../tools/FileTransferOps/LocalFrontierPrimaryEvaluations.cs).

The 60-second guard is not today's first failure; the 20-second guard is.
It is a dependency that a larger isolated selection allowance would encounter.
Changing either shared constant or borrowing the evaluator allowance is not
authorized by this review. Any later scoped-budget proposal must account for
selection, native visits, final custody consumers and ordinary rejection
without resetting or hiding the inherited work. No new numeric limit is
recommended from this incomplete prefix alone.

## Next bounded diagnostic step

Add an explicit selection-only component-profile option. Measure the existing
base, scope, file/facts and pin predicate invocations, retaining their original
arguments, order, short-circuit behavior, results and exception handling.
Sampling may select which invocations are timed, never which are executed.
Do not turn on the existing source-read instrumentation to obtain these
measurements, and do not run extra currentness checks for profiling.

Keep all timing overhead charged to the original clock. Emit only the existing
two bounded entry/return records while that clock is stopped, with fixed
component counters and sampled elapsed totals. Record sample counts so
overlapping or differently sampled intervals are not added or presented as
exact shares. Normal loaders and assemblies must exclude the new hooks;
default-off behavior and the unprofiled body must remain unchanged.

Verify explicit opt-in and normal exclusion, both hosts' small successful
selection, and the directly affected inherited-time, record-boundary, stale
owner and mutation rejections. Confirm identical visits and original outcomes
where timing does not force a budget rejection. Then run one fresh offline
component-profile scale fixture. Preserve its strict success assertion and
allow it to fail at the original bound. Do not repeat the normal scale case,
aggregate suites, portable build or GUI matrix for diagnostic-only changes.

Only the measured component should be considered for a subsequent surgical
optimization. That proposal must retain full fresh comparison, safe resource
lifetime and all original call points. If it cannot preserve those guarantees
or plausibly fit the remaining budget, prepare a separately scoped allowance
contract with the dependent consumers above; do not implement batching or a
shared-limit increase as an unreviewed fallback.

## Verification and remaining state

The [review receipt](../artifacts/native-selection-batching-review-20261002/verification.json)
records source inspection and hash preservation, not new test execution.
The previous twelve passing focused controls and failed scale gate remain
historical results. No concurrency reproduction, test, build, live GUI/NKN
run, runtime change, limit change, commit or push occurs in this review.
Content-family coverage, reported-candidate binding and production integration
remain incomplete; this review grants no new acceptance or coordinator authority.
