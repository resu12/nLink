# Native retained integration RCA and fix plan

2026-10-02. The first unresolved shared-custody edge is the cumulative time
policy, not incomplete scanning. The terminal probe completes both scans,
but ordinary selection still has a 20-second guard and later native consumers
have 20-second or 60-second guards on the same worker clock. Those later
consumers can close inputs after a long optional diagnostic.

The user authorizes necessary limit increases. Recommend a bounded increase
to 150,000 ms for content selection and 180,000 ms for retained native worker
operations after successful registration. Keep source admission at 20,000 ms,
all count/code-unit ceilings and all resource checks. This document defines
the next implementation slice; no code or limits change in this planning step.
The increase is an intentional diagnostic policy change, not a performance
optimization, authority grant or acceptance cutover.

## Evidence and cause

The [fresh terminal probe](file-transfer-native-terminal-selection-implementation-20261002.md)
enters with 18.3378403 seconds already charged, performs selection in
96.4820788 seconds and completes at 114.8199191 seconds. It inspects 102,218
source rows through two full scans and returns two witnesses. Resource checks
take 96.1045344 seconds within the 96.4741590-second scan interval. Those
overlapping measurements must not be added.

The 20-second content allowance cannot accommodate this measured workload.
Raising only that allowance still leaves `VisitEvaluation` at 60 seconds.
After selection, `Owns` and evaluation origin lookups call `CheckCurrent`,
which closes inputs on failed currentness. Source completion/origins, worker
currentness and payload reads additionally encounter producer-time checks.
The terminal fixture deliberately avoids those later consumers, so its pass
does not resolve the shared path. No ordinary acquisition or production
integration pass is inferred from it.

This is a measured reason to increase scoped time allowances. It is not a
reason to reset elapsed work, cache currentness, reduce validation frequency,
raise record or operation limits, or accept partial scans. The source remains
an identity/size fixture: its shape markers are not coordinator boundaries or
complete publication/security histories.

## Proposed time policy

Use one fixed private retained-work limit in `FrontierWorkerInputs`, not a
replacement for the global `LocalPublicationInputLimits` constants and not a
caller-controlled parameter. Retained eligibility follows successful original
native registration and completed source capture. The original owner,
source, pair, array/view and state checks still govern every operation.
Registration state chooses a time ceiling; it does not establish currentness
or grant route/publication authority.

| Operation or scope | Planned cumulative ceiling | Scope restriction |
| --- | ---: | --- |
| Attachment, source opening/begin/read, source publication and initial evaluation registration | 20,000 ms, unchanged | Original producer checks remain in place, including final admission checks. |
| Optional content selection | 150,000 ms | Includes inherited work, both scans, selection, hashing, origins and final checks. Default-off still performs no query work. |
| Native evaluation visits/currentness after registration | 180,000 ms | Same original counter and clock; no replacement evaluator or new evaluation invocation. |
| Worker/source retained currentness and origin lookup after registration | 180,000 ms | Before registration these APIs retain the producer ceiling; no source reopening or new source admission. |
| Native payload reads, payload/witness and transfer currentness after registration | 180,000 ms | Original receipts, single-use slots, byte checks and binding requirements remain mandatory. |
| Global producer/evaluator policy, parent-owned readers, parser, copy, bridge and transport | Unchanged | Different clocks/scopes must not inherit this increase. |

The difference between 150 and 180 seconds reserves 30 seconds of cumulative
headroom after optional time exhaustion. The observed run ends about 35.18
seconds below the content cap. Neither figure guarantees that all remaining
consumers or the future complete-history query will finish. Their actual work
must be measured. Retained exhaustion still fails closed at the new finite
limit; optional 150-second exhaustion alone must not close otherwise valid
custody or consume another diagnostic's slots.

Only exact ordinary scale/profile fixtures should receive proposed
210,000-ms worker and 240,000-ms outer attachment waits for verification.
These are wall-clock failure ceilings, not active work. Keep small fixture
waits unchanged, and keep the already reviewed terminal case at 180,000/
210,000 ms. No retries or automatic successive increases are proposed.

## Exact implementation sites

In [LocalFrontierPrimaryWorker.cs](../tools/FileTransferOps/LocalFrontierPrimaryWorker.cs),
add the fixed retained ceiling and a phase-aware retained currentness
predicate. Keep `ResourceCurrent` producer-scoped for attachment/admission.
Use the retained predicate only for the already-registered worker's `IsCurrent`
and retained getters. Preserve the same fresh base, scope, file/facts and pin
predicates, their order, callback invalidation and close behavior.

In [LocalFrontierPrimaryEvaluations.cs](../tools/FileTransferOps/LocalFrontierPrimaryEvaluations.cs),
raise native post-registration `VisitEvaluation` and `Current` time checks to
the retained ceiling. The `evaluation == null` visit branch, initial
`BeginEvaluation` admission and its final `CheckSource` stay at 20 seconds.
Do not change `EvaluationVisits`, array identity, native origins, view state,
one-shot `Take`, or the original `CheckCurrent` retirement policy.

In [LocalFrontierPrimarySource.cs](../tools/FileTransferOps/LocalFrontierPrimarySource.cs)
and [LocalFrontierPrimaryOrigins.cs](../tools/FileTransferOps/LocalFrontierPrimaryOrigins.cs),
use the phase-aware ceiling only for retained completion/origin consumers.
Keep source opening, begin, fragments, reads and line/EOF publication under
the producer guard. Origin batches still require 1–64 original tuples, full
entry/exit validation and per-tuple abort checks; no successful prefix escapes
on failure. Both full and abort-only time checks must use the matching retained
ceiling after registration, not a mixture of 180 and 20 seconds.

In [LocalFrontierPrimaryPayloads.cs](../tools/FileTransferOps/LocalFrontierPrimaryPayloads.cs),
give already-bound payload reads an explicit retained resource check at the
existing entry, fragment and exit sites. Do not globally change `CheckSource`,
which would also relax source admission. Hashing, strict decoding, byte clearing
and failed-capture cleanup remain unchanged. Witness consumers reuse native
evaluation currentness; inspect
[LocalFrontierPrimaryWitnesses.cs](../tools/FileTransferOps/LocalFrontierPrimaryWitnesses.cs)
for any remaining 20/60-second dependency rather than consuming or replacing
their original slots. Update only retained transfer currentness in
[LocalFrontierPrimaryTransfer.cs](../tools/FileTransferOps/LocalFrontierPrimaryTransfer.cs);
initial transfer attachment remains producer-scoped.

[LocalFrontierPrimaryReaders.cs](../tools/FileTransferOps/LocalFrontierPrimaryReaders.cs)
contains `FrontierPrimaryInputs.OwnedReader`, with a parent-side clock. It is
not a consumer of `FrontierWorkerInputs.work`. Leave it unchanged. This
distinction narrows the earlier consumer inventory; similarly named primary
partials are not evidence that their clocks or allowances are shared.

In the [test acquisition adapter](../tests/nLink.SmokeTests.Core/Support/NativeContentAcquisition.cs),
raise the ordinary optional ceiling to 150 seconds, retaining native visit
charging and the same inspections. Do not invoke the terminal wrapper to make
ordinary selection pass. Update the ordinary time-exhaustion control to cross
the new content bound and preserve its following `Owns`/origin assertions.
The terminal adapter remains a separate diagnostic, not a production seam.

## Verification sequence

First add focused controls for the changed contract. Demonstrate that source
read and initial registration still reject beyond 20 seconds; retained native
currentness/origins can operate after 60 seconds; optional exhaustion beyond
150 seconds returns unproven with valid later custody; and retained exhaustion
beyond 180 seconds rejects with the original close behavior. Preserve copied
owner/pair/array/view rejection, stale/cancelled scope and byte-mutation checks,
record/code-unit limits, default-off behavior and unchanged slot ownership.
Synthetic elapsed values belong only in small controls, never the scale run.

Then run only the affected native source/origin/evaluation/payload/witness/
transfer controls. Verify that ordinary content rejection does not prevent
the required later original operation, not just that slot fields remain empty.
Retain original input/result invariance and full/compact identity checks.
Do not add an authority evaluation, rerun a correlator, or use getter loops as
uncharged diagnostic work. All new query work and final verification must use
the original cumulative clock, including inherited charges.

Finally rebuild the affected test assembly and run one ordinary, non-terminal
scale-profile case. Require both scans, exact witnesses, post-selection
`GetPhysicalOrigin`/`Owns`, retained source validity, bounded final cumulative
work and complete untruncated streams. Keep the original strict success
assertion; do not turn a failure into an expected outcome. Pin launch sources
and all actual charges. If another bound or consumer fails first, stop at that
edge and prepare its RCA without rerunning unrelated lanes.

## Subsequent integration gates

A successful shared size/custody case would still not prove complete content
acquisition. Next acquire semantic-family provenance and bind the original
already-reported mismatch candidate, then exercise complete relevant security/
publication histories on real-sized inputs. Preserve uniqueness, malformed
record handling, original physical origins and independent full/compact
bindings. Do not raise record/code-unit/operation ceilings prospectively;
the user's permission permits a justified later increase, not removal of bounds.

The [production integration contract](file-transfer-content-production-integration-review-20261001.md)
still governs those non-time requirements. Its 20-second optional-work clause
is superseded only for the next scoped slice by the user's limit authorization;
its completeness, custody, no-added-evaluation and diagnostic-only requirements
remain. A default-off production content-query seam comes only after those
acquisition gates, without loading test-support code. A portable rebuild and
targeted live-256 test remain later. Acceptance cutover and a full GUI matrix
are separate gates.

## Plan verification and authorization

The [plan receipt](../artifacts/native-retained-integration-plan-20261002/verification.json)
pins the reviewed sites and measurements, verifies current terminal evidence
and launch sources, and preserves the accumulated dirty files and normal
assemblies. This is a documentation-only step: no tests, build, scale rerun,
portable package, live GUI/NKN run, commit or push occurs. Current production
limits, existing failed shared-scale verdict, route authority, `Recovered`
and report acceptance remain unchanged. The next bounded implementation may
use the authorized scoped time increases without another routine approval.
