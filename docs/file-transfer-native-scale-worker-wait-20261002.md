# Offline native scale worker wait and selection failure review

2026-10-02. The approved 90-second attachment-signaling wait is implemented
only for the two exact offline scale fixtures. Twelve focused controls pass.
One normal scale run now completes the original source read and reaches
content input selection, but fails its selection-result assertion. The wait
blocker is resolved; the acquisition gate remains failed. No active-work
limit, authority check, or acceptance rule changes in this step.

## Implemented scope and controls

The parent selects 90 seconds for exactly `content-native-scale` and
`content-native-scale-profile`, using case-sensitive comparison. All other
worker cases retain 30 seconds, including similarly named cases and the
ordinary timeout control. Output-capture metadata records the selected
attachment barrier; its default remains 30 seconds for existing callers.
The exit wait remains 30 seconds and cleanup remains 5 seconds.

The new selection tests fail on both PowerShell hosts before implementation
and pass afterward. Twelve distinct controls then pass: two exact-case
selection tests, two bounded and nonblocking output-capture tests, two
ordinary deadline controls, two small native acquisition controls, and four
parent-handoff compatibility controls. Both ordinary deadline tests retain
their 30-second rejection and cleanup behavior. Pending and faulted output
tasks are still not awaited, and each stream retains at most 65,536 characters.

The producer allowance remains 20 seconds of charged active work, shared
adoption remains 15 seconds, and the local owned cursor parser remains
10 seconds. Record, byte, witness, cancellation, identity, publication,
native predicate, and coordinator-authority rules are unchanged. Core and
Infra assemblies remain byte-identical to the initial checkpoint. Only the
test assembly changes to include the regression assertions.

## Fresh unprofiled scale result

The single normal scale fixture fails after reaching the following stages.
These are fixture wall-clock intervals, not live throughput measurements or
charged active-work measurements.

| Stage | Interval in milliseconds |
| --- | ---: |
| Original source read and event conversion | 57,522 |
| Original full and compact array materialization | 4,489 |
| Original evaluation creation and recording | 18 |
| Original evaluation pair take | 5 |
| Content input selection call | 1,847 |

The worker returns from selection at 64,936 ms from its stage clock start,
then fails `Content original input selection missing`. Cleanup returns at
64,948 ms. The parent records `Deadline=false` and `Phase=validate_output`,
with the selected barrier at 90,000 ms. Both output tasks complete without
truncation. There are 23 bounded, ordered stage records and no added source
timing profile records.

Code order establishes that the original source completeness assertion,
evaluation recording, non-null pair assertion, and default-off check all
pass before selection starts. Earlier processing also succeeds: local
processing takes 5,208 ms, shared cumulative work reaches 7,421 ms, and the
208,326,025-byte semantic copy returns at 9,964 ms cumulative work. Semantic
and analyzer outputs each contain 102,218 lines and have matching actual
hashes. Those earlier gates do not establish selection or live acceptance.

## RCA boundary and next fix plan

The former 30-second parent barrier prevented observation of source EOF.
The fresh run resolves that missing edge and advances to the first unproven
condition: a prepared native input selection with two witnesses and exactly
one callback. The current assertion combines status, witness count, and
callback count. It does not retain the returned result before throwing.
Therefore this artifact cannot identify which condition failed, the inner
rejection reason, or the cumulative active work at rejection. Producer-budget
expiry is a hypothesis, not a demonstrated cause. Another limit increase is
not supported by this result.

Next, add one bounded fixture-only diagnostic record immediately after the
existing selection call and before the unchanged assertion. Retain only the
returned result's status, first missing edge, record and code-unit visits,
cumulative milliseconds, witness count, and callback count, with an explicit
null-result state. Do not perform another selection, authority evaluation,
source pass, currentness check, or clock mutation. Verify small successful
and existing rejection controls, then rerun one normal scale fixture. Use
that returned reason to choose a surgical fix; keep all limits unchanged
until its cause and contract implications are established.

The [verification receipt](../artifacts/native-scale-worker-wait-20261002/verification.json)
pins the fresh failure and source checkpoint, preserves 66 previous scale
artifact pins and 99 historical live artifact pins, and leaves all historical
verdicts unchanged. Content coverage and production integration remain
incomplete. No live GUI/NKN run, portable rebuild, full matrix, commit, or
push occurs.
