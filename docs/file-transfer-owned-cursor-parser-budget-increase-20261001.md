# Owned cursor parser budget increase

2026-10-01. The approved local allowance increases from 5,000 to 10,000 ms
only when the parser uses both cursor mode and the original native owned
source type. Generic readers, generic cursor readers and non-cursor owned
readers retain the 5,000 ms local allowance. The original cumulative
active-work cap remains 10,000 ms, so earlier work still reduces the time
available to parsing and later operations.

The single normal-path scale rerun now completes the original processing
workflow. It then fails at the original semantic-evidence-copy gate. The
requested limit change is implemented and verified; the overall acquisition
gate and production integration remain incomplete.

## Implementation scope

`GuiSmokeLedgerStreamRead` selects a readonly local limit when constructed.
Only an owned cursor selects `LocalFrontierFileAdoption.MaximumActiveMilliseconds`;
all other readers keep 5,000 ms. Both existing full and buffered local-clock
checks use that selected value. The comparison operators, short-circuit
order, validation points, source retirement and rejection behavior are
otherwise unchanged.

Reversing these threshold-only edits reproduces the exact previous processor
file hash. The cumulative clock is neither reset nor extended. Managed wait
APIs, native cancellation and process checks, fresh file/channel validation,
byte/line limits, content selection bounds and route authority remain unchanged.
The unchanged polling contract is documented in the
[polling review](file-transfer-native-polling-contract-review-20261001.md).

## Focused verification

Eight new tests run on PowerShell 5.1 and PowerShell 7. Clock-controlled
original-owner cases accept a buffered owned-cursor read after advancing its
local clock to six seconds, then reject and retire the source when the local
clock reaches eleven seconds, the cumulative clock reaches eleven seconds,
or cancellation occurs. Separate generic and generic-cursor cases still
reject at six seconds. Existing non-cursor local-clock controls retain their
original six-second rejection.

After stabilizing the test harness, the relevant lane passes 36 tests: the
eight new tests and all 28 existing buffered-reader regressions. Those
regressions cover custody, cloning, cancellation, local/cumulative deadlines,
completion and output boundaries. This is 36 unique passing tests, not 44
from counting the initial eight-test development run again. No unrelated
aggregate contract, analyzer or live GUI matrix is run.

## Targeted scale result

The new normal-path fixture reports `ProductionProcessingPath=true` and
`Processed=true`. Its observed processing workflow takes 5,531 ms, and its
cumulative active work is 7,682 ms immediately after processing. The first
number measures the whole processing call, not isolated parser CPU time.
Both the local owned-cursor allowance and cumulative cap are recorded as
10,000 ms.

The next call, `TryCopySemanticEvidence`, returns no owned copy. The fixture
reports `Parent registration original semantic copy missing`, and the single
targeted scale test fails at that gate. Native content selection is not reached.
This is progress past parsing, not successful acquisition or route acceptance.
The exact rejection predicate within semantic copying has not been localized;
do not attribute it to a particular deadline or change copying behavior from
this result alone.

The original semantic temporary output is 208,223,808 bytes, while the failed
destination contains 189,431,808 bytes. A partially created file is not an
owned success receipt and is not accepted. The unchanged cumulative cap and
all custody checks remain in force. These are offline size-fixture observations,
not a live throughput benchmark or genuine content-family coverage proof.

## Checkpoint and next step

The [verification receipt](../artifacts/native-parser-budget-20261001/verification.json)
pins the implementation, test outcomes and fresh scale artifacts. Accumulated
changes outside this slice, normal Core/Infra assemblies and all 99 historical
live artifact pins remain unchanged. The test assembly is rebuilt for the new
tests. Previous receipts retain their original 5,000 ms results and are not
rewritten as evidence for the new policy.

Next: localize the first rejection inside the original semantic-copy operation
using this fresh artifact before preparing a separate fix. Do not raise the
cumulative cap, weaken copying/custody checks or accept the partial destination
as a workaround. Content-family completeness, reported-candidate binding and
historical route verdicts remain unchanged and unproven where previously
unproven. No portable rebuild, live GUI/NKN run, commit or push occurs in this
slice.
