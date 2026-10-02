# Native selection component profile and allowance review plan

2026-10-02. The fresh offline profile attributes 60.8% of sampled resource
component time to file/facts validation and 32.8% to retained file pins.
Selection still exhausts the unchanged 20-second cumulative guard before
its first full scan completes. The diagnostic implementation is verified;
the scale acquisition gate remains failed.

Recommend a separately scoped allowance-contract review next, including
the downstream 60-second evaluation guard and valid shared-custody consumers.
The measured cost is spread across components: the sampled prefix does not
support expecting a single-component optimization to fit the remaining
2.12 seconds. This is a review recommendation, not a new allowance, a timing
bound, or permission to reduce validation frequency.

## Implemented diagnostic and focused verification

The [test loader](../tests/nLink.SmokeTests.Core/Support/NativeContentAcquisitionLoader.ps1)
adds an explicit `SelectionComponentProfile` option. It enables the existing
selection profile and a separate component compilation symbol, without the
source-read profile. Combining it with the source-read profile is rejected.
The exact existing scale-profile fixture now requests component profiling;
its original success assertion and 90-second worker attachment wait remain.
Ordinary selection-profile cases keep their previous behavior.

The new conditional branch in
[ResourceCurrentWithin](../tools/FileTransferOps/LocalFrontierPrimaryWorker.cs)
retains close/time checks and invokes each original base, scope, file/facts
and pin predicate exactly once, in the original short-circuit order. The
[profile helper](../tests/nLink.SmokeTests.Core/Support/NativeContentSelectionProfile.cs)
counts only the original owned selection window and times every 128th
resource call. It never performs an additional native check or grants
currentness. Before selection starts and after profiling ends, component
counters and timers are inactive; any hook overhead remains charged normally.

Only the existing two bounded entry/return records are emitted, with the
original work clock stopped. No original clock is started, stopped, reset
or extended by the profiling helper. No source pass or authority evaluation
is added. Removing the new conditional branch reproduces the exact previous
native worker source; removing the helper's conditional blocks reproduces
the previous selection helper modulo formatting whitespace. The original
acquisition source is byte-identical.

Twelve focused controls pass. Eight new controls cover explicit loading on
both hosts and six paired comparisons between ordinary and component
selection profiles: valid inputs on PowerShell 5.1 and 7, inherited time
exhaustion, record exhaustion, stale owner and mutation during selection.
They preserve scan/row/check/resource counts and native evaluation visits.
Separate fixture paths and source contents can change code-unit totals;
byte-identical acquisition code preserves the original charging logic.
Four compatibility controls cover the existing selection loader and ordinary
positive acquisition on both hosts. The new loader tests first failed as
expected before the option existed; that red receipt is preserved.

Normal Core/Infra binaries remain byte-identical. The normal test assembly
was rebuilt for the new controls. Both profile helper types remain absent
from normal assemblies. One production-tool source path receives a test-only
conditional branch; its normal compiled behavior is unchanged.

## Fresh scale result

The profile starts with 17.8789386 seconds already charged, leaving
2.1210614 seconds. It charges another 2.1225213 seconds and returns
`unproven` with `content_input_budget` at 20,001 cumulative milliseconds.
It completes 4,491 of 102,218 full rows, or 4.39% of the first scan.
There are 17,967 original resource calls. The primary selector and second
scan have not run; zero callbacks and witnesses are returned.

The initial scan takes 2.1217171 seconds, including 2.1127051 seconds in
full resource checks, or 99.5752% of scan time. These intervals overlap;
they must not be added together. Entry and return records both observe the
work clock stopped, and entry component counters are zero.

| Resource component | Original calls | Timed samples | Sampled ticks | Mean sampled microseconds | Share of summed component samples |
| --- | ---: | ---: | ---: | ---: | ---: |
| Base ownership and parent/request lifetime | 17,967 | 141 | 6,800 | 4.82 | 4.16% |
| Request mapping | 17,967 | 141 | 3,638 | 2.58 | 2.23% |
| File and facts channels | 17,967 | 141 | 99,462 | 70.54 | 60.84% |
| Retained file pins | 17,967 | 141 | 53,593 | 38.01 | 32.78% |

The stopwatch frequency is 10,000,000 ticks per second. Each component has
the same 141 selected calls, so these shares compare their disjoint sampled
intervals. They are not exact shares of the entire scan or of all resource
calls. Fixed-stride samples can miss other latency patterns, and timing and
counter overhead remain included. The file/facts measurement does not split
channel polling, mapped-byte comparison or acknowledgment checks; the pin
measurement does not split file-type and file-information calls.

Earlier processing and the 208,326,024-byte semantic copy succeed. Persisted
semantic and analyzer copies agree in actual SHA-256, byte count and
102,218-line metadata. Both worker streams complete without truncation, and
the parent fails at output validation without reaching its attachment
deadline. These are offline custody/size observations, not content-family
coverage or live acceptance.

## Cause and recommended contract review

Each completed full row requires four fresh resource checks, and selection
requires two full scans plus its compact/witness/origin work. The repeated
checks, not row membership bookkeeping, consume almost all measured time.
The local locks and asynchronous close callbacks do not authorize removing
those checks; the
[batching contract review](file-transfer-native-selection-batching-contract-review-20261002.md)
identified a change-and-restore counterexample to that proposal.

A linear extrapolation of this prefix gives approximately 48.3 seconds for
one full scan and 96.6 seconds for two, before inherited work and other
selection operations. Combining that projection with the sampled component
distribution suggests about 37.8 seconds would remain even if file/facts
cost disappeared entirely. Even removing both file/facts and pin cost would
leave roughly 6.2 seconds. Those removals are not valid optimizations; they
are optimistic cost comparisons, not lower bounds or completion forecasts.
They explain why an isolated component fix is not a demonstrated route to
the available 2.12-second headroom. Variation from the previous profile's
row count does not establish an improvement; no optimization was made.

The next review must inventory the same cumulative clock across producer
selection, `VisitEvaluation`, final `Owns`/origin validation and any remaining
frontier diagnostic. `VisitEvaluation` and evaluation currentness retain a
60-second cumulative guard. Raising only a selection parameter can move
failure to that guard or cause later custody validation to close otherwise
usable shared inputs. Raising a shared constant can affect unrelated source
or evaluation admission. Neither is an isolated fix by default.

Propose a finite diagnostic allowance only after defining its consumers,
inherited-work accounting, rejection/retirement behavior and preservation
of the original frontier operations. Keep per-row currentness, full byte
comparisons, record/code-unit ceilings, 1–64 witnesses, original references,
default-off behavior and coordinator authority. Do not reset elapsed work,
borrow the evaluator allowance, accept incomplete scans or make ordinary
optional-budget exhaustion retire valid shared custody. If these requirements
cannot coexist with a larger scoped allowance, report the incompatibility
before implementation. No new numerical limit is approved here.

## Verification receipt and remaining work

The [verification receipt](../artifacts/native-selection-components-profile-20261002/verification.json)
pins the focused results, component measurements and 88-file launch source
checkpoint, and verifies preserved earlier scale and historical live evidence.
The strict failing scale assertion remains unchanged. No aggregate suite,
normal scale rerun, portable rebuild, live GUI/NKN run, commit or push occurs.
Content-family coverage, reported-candidate binding and production integration
remain incomplete. Limits, runtime authority, acceptance and prior verdicts
are unchanged.
