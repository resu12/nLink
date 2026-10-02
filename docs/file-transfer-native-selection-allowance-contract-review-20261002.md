# Native selection allowance contract review

2026-10-02. A selection-only limit increase is incompatible with the current
shared-custody contract. Selection and later frontier operations charge the
same stopwatch, but later operations retain 20-second or 60-second guards.
Some failed currentness checks close the shared inputs. Raising selection
alone therefore moves the failure; it does not preserve later consumers.

Recommend a separate, terminal-only offline size/custody probe with a proposed
150,000-millisecond cumulative allowance. This is a narrower diagnostic
contract, not a fix for general integration or a replacement for the strict
scale gate. Implementation requires approval of that distinction. No limit,
wait, code, acceptance rule or previous verdict changes in this review.

## Observed failure and estimate

The [fresh component profile](file-transfer-native-selection-component-profile-rca-20261002.md)
returns `unproven` / `content_input_budget` at 20,001 cumulative milliseconds.
It enters selection with 17.8789386 seconds charged and completes 4,491 of
102,218 rows in its first scan. Neither the selector nor the second scan runs.
File/facts and pin validation account for most sampled component time.

Linear projection gives approximately 48.3 seconds per full scan and 96.6
seconds for two scans. Adding the observed inherited work gives approximately
114.5 seconds, before other selection work or new pre-entry assertions. This
is an estimate from a short profiled prefix, not a bound or a completion
guarantee. The proposed 150-second ceiling supplies finite diagnostic
headroom; it must still reject incomplete work. It is not yet approved.

## Shared-clock consumer inventory

The limits below apply to the original `FrontierWorkerInputs.work` clock.
It accumulates across operations; stopping it between operations does not
clear inherited time. Parent-side clocks are not interchangeable with it.

| Consumer | Current cumulative guard | Consequence relevant to an increase |
| --- | --- | --- |
| Native source admission, source completion/origins, retained reads and worker `IsCurrent` | 20,000 ms | Rejection paths can close inputs; finishing a larger selection does not restore admission. |
| Evaluation registration | 20,000 ms while constructing custody | Original registration still must finish within producer admission. |
| Content cursor checks and fresh resource validation | 20,000 ms | Current first missing edge; inherited time is included. |
| Content cursor `Visit` through native `VisitEvaluation` | 60,000 ms after registration | A larger cursor allowance alone still reaches `frontier_evaluation_budget`. |
| `Owns`, evaluation physical-origin lookup, payload/witness currentness | 60,000 ms | `CheckCurrent` closes inputs when currentness fails. |
| Payload capture and witness resolution/capture | 60,000 ms plus 20,000-ms payload reads | These consumers cannot safely follow a 150-second selection under their current contract. |
| Transfer binding/currentness | 60,000 ms, with producer admission elsewhere | They remain unavailable after a larger cumulative selection; no transfer authority follows from the probe. |

Source anchors are the
[limits](../tools/FileTransferOps/LocalNativeInputCapture.cs),
[worker resource checks](../tools/FileTransferOps/LocalFrontierPrimaryWorker.cs),
[source](../tools/FileTransferOps/LocalFrontierPrimarySource.cs),
[origin batches](../tools/FileTransferOps/LocalFrontierPrimaryOrigins.cs),
[retained readers](../tools/FileTransferOps/LocalFrontierPrimaryReaders.cs),
[evaluation currentness and retirement](../tools/FileTransferOps/LocalFrontierPrimaryEvaluations.cs),
[payloads](../tools/FileTransferOps/LocalFrontierPrimaryPayloads.cs),
[witnesses](../tools/FileTransferOps/LocalFrontierPrimaryWitnesses.cs) and
[transfer](../tools/FileTransferOps/LocalFrontierPrimaryTransfer.cs).

The [current test worker](../tests/nLink.SmokeTests.Core/Support/NativeContentAcquisitionWorker.ps1)
calls `GetPhysicalOrigin` for successful rows and `Owns` after selection.
Those are real budget-bearing consumers, not passive assertions. The current
fixture therefore does not satisfy a terminal-only contract. State `taken`,
a test-mode flag or an unused witness slot does not establish terminality.

## Proposed terminal-only contract

Use a dedicated test-only fixture and entry point, default off, with a fixed
150,000-ms total allowance. Keep the existing adapter and ordinary fixtures
unchanged. Do not add a caller-configurable general allowance. The test
harness must own the complete remaining call sequence and demonstrate that
no budget-bearing source, evaluation, payload, witness or transfer consumer
remains after entry. No equivalent production lifecycle proof exists today.

Before terminal entry, finish any required origin oracle and original
currentness assertion under their existing guards. Their work remains
charged. A pre-entry oracle is only an independent test expectation; it is
not authority for accepting a later row. Entry still requires the original
owner, request, issued pair, context, arrays and views. If pre-entry work
fails, do not enter the probe or relax that original guard.

During selection, retain both full scans, all original per-row resource
checks, byte comparisons, mutation rejection, origin mapping, ordered 1–64
witnesses and selected-event inclusion. Keep input, record, code-unit and
native evaluation-visit ceilings unchanged. Use the original cumulative
clock for every charged operation; no reset, subtraction, replacement clock
or uncharged preparation is permitted.

The terminal cursor would need its own test-only visit path: increment the
same native evaluation counter and enforce its existing count ceiling, but
check the explicit terminal time allowance instead of invoking the ordinary
60-second `VisitEvaluation` path. This is an intentional change to the
dedicated diagnostic time contract, not borrowing the evaluation allowance.
Ordinary evaluation and content paths must retain their original guards.

At return, require the second full scan and final fresh resource/time checks
to complete successfully. Return only detached diagnostic rows. Subsequent
checks may inspect those rows, ended-cursor rejection and passive slot
bookkeeping, then use normal cleanup/disposal. They must not call `Owns`,
`GetPhysicalOrigin`, source completion or other shared currentness APIs.
Pre-entry currentness cannot be reported as proof of post-return usability.

Ordinary optional omission/budget exhaustion must continue to return
`unproven` without explicitly retiring shared custody or consuming frontier
slots. The terminal probe also must not call `Close` merely to suppress a
budget failure; its normal final disposal is cleanup, not success evidence.
After a long terminal attempt, later shared-custody usability is explicitly
outside this diagnostic contract. If any such consumer is required, reject
terminal eligibility and retain the existing contract.

A proposed 180,000-ms attachment deadline may apply only to the new terminal
scale case. It is a separate wall-clock failure ceiling, not extra active
work. Existing scale/profile cases keep 90,000 ms, stream bounds and strict
assertions. A deadline or incomplete capture remains a failure. Do not add
automatic retries, successive limit increases or a fallback acceptance path.

## Alternatives rejected and approval boundary

Changing only the cursor's 20-second constant leaves the native 60-second
visit guard and post-selection consumers unresolved. Raising shared
producer/evaluation constants changes unrelated admission. Resetting or
subtracting diagnostic elapsed time conceals cumulative work. Reducing
checks changes mutation rejection, including the change-and-restore case
identified in the [batching review](file-transfer-native-selection-batching-contract-review-20261002.md).
None is an isolated preservation of the reviewed contract.

The terminal proposal can measure large-input selection without asserting
future shared-custody usability. It cannot establish content-family coverage,
reported-candidate binding, native writer custody, publication completion,
coordinator authority or live acceptance. The original scale gate remains
failed at `content_input_budget`; a successful terminal probe would be a
separate diagnostic result, not permission to relabel that gate as passed.
The [production integration review](file-transfer-content-production-integration-review-20261001.md)
remains unresolved.

After explicit approval of this narrower contract, implement focused controls
for terminal eligibility/call order, inherited elapsed accounting, time and
count exhaustion, default-off behavior, stale ownership, array/view identity,
mutation, detached rows and unconsumed slots. Preserve ordinary post-selection
`Owns`/origin controls. Verify normal compiled behavior is unchanged, then run
one new offline terminal scale probe. Inspect its first missing edge before
any further change; do not run a live test or full matrix for this step.

## Review verification

The [review receipt](../artifacts/native-selection-allowance-review-20261002/verification.json)
checks preservation of the initial dirty files, launch source checkpoint,
normal assemblies and latest scale artifacts. The projected 114.5 seconds
and proposed 150/180-second ceilings are recorded separately from measured
results. This is documentation-only verification: no tests, build, scale
rerun, portable package, live GUI/NKN run, commit or push is performed.
