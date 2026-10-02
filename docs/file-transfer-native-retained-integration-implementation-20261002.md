# File transfer retained native integration implementation

The scoped budget change is implemented and verified. Ordinary optional
selection now completes on the original native source and leaves subsequent
custody and origin operations valid. The single non-terminal offline scale
test passed. Content-family coverage, reported-candidate binding and production
integration remain unproven; this result does not change route acceptance.

This implements the approved
[RCA and fix plan](file-transfer-native-retained-integration-rca-fix-plan-20261002.md).
The [verification receipt](../artifacts/native-retained-integration-20261002/verification.json)
records source, assembly, test and artifact hashes.

## Scoped time policy

Admission remains limited to 20,000 milliseconds. Attachment, source opening,
begin, fragments, reads, line and EOF publication, and initial evaluation
registration still use that guard. Optional input selection has a fixed
150,000-millisecond ceiling. Successfully registered retained worker consumers
have a private 180,000-millisecond ceiling. All three use the original
cumulative worker clock; no work is reset, subtracted or transferred to a
second clock.

[Worker currentness](../tools/FileTransferOps/LocalFrontierPrimaryWorker.cs),
[evaluation custody](../tools/FileTransferOps/LocalFrontierPrimaryEvaluations.cs),
[retained source completion and scalar origins](../tools/FileTransferOps/LocalFrontierPrimarySource.cs),
[batched origins](../tools/FileTransferOps/LocalFrontierPrimaryOrigins.cs),
[payload reads](../tools/FileTransferOps/LocalFrontierPrimaryPayloads.cs) and
[retained transfer currentness](../tools/FileTransferOps/LocalFrontierPrimaryTransfer.cs)
use the retained policy at their existing validation sites. Registration selects
only a time allowance, not resource authority. Fresh checks, original-reference
guards, close behavior and operation slots remain intact. Witness consumers
inherit evaluation currentness; their production implementation was not edited.

The [test-only acquisition adapter](../tests/nLink.SmokeTests.Core/Support/NativeContentAcquisition.cs)
uses the optional ceiling without invoking the terminal wrapper. Exact ordinary
scale and scale-profile cases receive worker and outer attachment waits of
210,000 and 240,000 milliseconds. Small-case waits remain 30,000 and 20,000;
terminal-scale waits remain 180,000 and 210,000.

Global producer and evaluation constants remain 20,000 and 60,000 milliseconds.
Parent-side readers, record and code-unit limits, resource-check frequency,
route authority and GUI acceptance are unchanged.

## Focused verification

Seventy distinct focused and compatibility checks passed. The first run passed
19 checks and exposed two stale source-projection assertions. One pinned the
previous source policy; the other did not handle the already-existing component
profile branch. The assertions now pin the reviewed projection and handle that
branch without weakening the behavioral guards. The rebuilt contract lane
passed all 41 selected checks, including both projection checks; the final
compatibility lane passed all 10 selected checks.

Small controls establish that reads and registration still reject beyond
20 seconds, retained operations work at 70 seconds, optional exhaustion at
151 seconds returns unproven while actual original payload reading and witness
capture remain available, and retained exhaustion at 181 seconds closes the
original custody. An invalid scope still rejects at 70 seconds. Copied inputs,
mutated arrays, stale ownership, native pin failures, count and character
limits, default-off behavior, one-shot operations and independent transfer
currentness are covered by the selected checks. Synthetic elapsed values are
confined to small controls, never the scale fixture.

Test receipts are retained as
[first focused run](../artifacts/native-retained-integration-20261002/retained-focused-first.trx),
[affected contract lane](../artifacts/native-retained-integration-20261002/retained-contract.trx)
and [compatibility lane](../artifacts/native-retained-integration-20261002/retained-compatibility.trx).

## Ordinary scale result

The [single scale test](../artifacts/native-retained-integration-20261002/retained-scale.trx)
passed in 3 minutes 39 seconds against the
[110-source checkpoint](../artifacts/native-retained-integration-20261002/scale-source-checkpoint.json).
It used 102,218 original semantic rows and a 208,326,024-byte semantic source.
The analyzer copy matches that source byte-for-byte and by SHA-256.

The original clock carried 18.0358134 seconds into selection. Selection added
95.8404302 seconds and returned prepared input selection with two witnesses and
one callback at 113.875 seconds cumulative. Post-return original evaluation
ownership, physical-origin joins, worker currentness, source completion and
source-origin batching passed; final cumulative work was 113.888 seconds with
the clock stopped. Native evaluation visits reached 1,226,632 without resetting
inherited accounting.

Both complete scans inspected 204,436 full rows. Selection performed all
817,769 fresh resource checks, with the same count for each original base,
scope, file and pin component. No check was cached or removed. Worker output
is complete and untruncated, with no stderr or attachment deadline. These
observations establish input selection and retained custody only, not semantic
coverage, candidate binding, throughput or route correctness.

## Preservation and next step

The final audit preserves 116 out-of-scope baseline pins, all 110 launch-source
pins and both historical scale artifact sets. The earlier strict failure remains
a historical failure; it is not relabeled using this result. Core and Infra
assemblies are unchanged; the affected test assembly was rebuilt. Native
acquisition/profile helper types remain absent from all three normal assemblies.

No aggregate suite, portable package build, live NKN run, full GUI matrix,
commit or push was performed. The next proof edge is source-owned content-family
coverage and reported-candidate binding. Review that bounded contract before
extending the query; a new live test is not yet required.
