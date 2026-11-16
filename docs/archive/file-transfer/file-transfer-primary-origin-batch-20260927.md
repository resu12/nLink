# Bounded primary-origin batches

Date: 2026-09-27. The user approved the bounded validation-cadence contract in
`file-transfer-primary-origin-validation-review-20260927.md`. Implementation and
50 focused checks are complete. The single rebuilt offline canonical run clears
the origin-budget edge, then fails at a later fixture binding/cleanup guard.
The original 20,000 ms cumulative work limit is unchanged.

## Implementation

`SourceReader.GetFrontierPhysicalOrigins` accepts 1–64 ordinal/string tuples.
It retains the original native-reader identity gate, owner/inputs lock order,
genuine EOF requirement, exact emitted-string reference checks, native physical
origin mapping, and original cumulative work clock. Scalar origin behavior is
unchanged.

Full resource validation occurs at entry and before publication. Each tuple
checks closed/budget/base/scope/cancellation state; these checks can only abort.
A final liveness/budget check follows the closing full validation. Each tuple
is read once into locals. The result stays private until the entire batch
succeeds; invalid requests return no result without retiring healthy custody,
while resource failure follows the existing close path.

The existing native analyzer fixture consumes every event in batches of at
most 64 and fails immediately on a rejected batch. The ordinary source branch
still uses the scalar API. Complete source/origin assertions and analyzer pass
counts remain enabled. No new ownership, route, evaluation or proof authority
is introduced.

The approved tradeoff remains explicit: a transient facts/pin mutation fully
restored between boundary scans may be observed less often. This is bounded
validation, not continuous byte verification or a cached cross-batch verdict.

## Focused verification

- 29 batch checks pass: sizes 1/63/64, rejection of 65 and invalid shapes,
  repeated/out-of-order tuples, genuine EOF, copied reader, substituted strings,
  mutable request arrays, and scope/facts/pin/cancel/dispose/budget failures
  before, during, at closing, and after a successful batch. A separate check
  expires the budget after closing validation. Failed batches expose no prefix.
- 21 compatibility checks pass: 15 scalar/currentness diagnostics, two default
  loader isolation checks, and four native/ordinary merge controls across
  Windows PowerShell and PowerShell 7. Default scalar/worker projections retain
  their sealed hashes. Fault-injection hooks and counters are profile-only.
- PowerShell parse checks and the scoped diff check pass.
- Full non-incremental smoke build succeeds: 357 warnings, zero errors,
  20.29 seconds. Warning details were suppressed by the ErrorsOnly console;
  this report does not claim a warning-free build or classify those warnings.

The first batch-test attempt was interrupted after five profile compilation
failures (`CS0649` on the uninitialized test hook). Explicitly initializing that
test-only field to null fixed compilation; the complete 29-case rerun passed.
No runtime test failure was hidden or reclassified by that correction.

Results are under `artifacts/primary-origin-batch-20260927/results/`.

## Canonical verification and preservation

Artifact:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-e73076df5ad74c46ac1ec661043f80a7`.

All 73,671 events retain positive, strictly increasing physical origins, from
physical line 5 through 101,796. The primary consumes the complete vector in
1,152 batches (the final batch has seven entries). Genuine EOF is observed:
73,671 lines and 69,673,434 code units. No scalar origin calls are substituted.

| Original cumulative work component | Seconds |
| --- | ---: |
| Attach | 0.0347834 |
| Open | 0.0009726 |
| Read | 18.1273866 |
| All origin batches | 1.1293634 |
| Total after origin batches | 19.2925060 |

The components reconcile exactly. After origins, 0.707494 seconds remain in the
unchanged limit. Final cumulative work is 19.3224833 seconds. Original resource
and base rejection diagnostics remain `none`; the first close is `Dispose`,
with true EOF. This demonstrates progress in this run, not a performance margin
guarantee under different source counts or scheduling. The facts body is
1,927,447 bytes, without an acknowledgment channel.

The overall integration still fails: two analyzer evaluations, one native
parser invocation, no complete proof artifacts, and no parity result. Primary
PID 2472 exits 1 without timeout. The reported exception is
`Original evaluation retirement did not independently close actual payload handles`
at `FrontierAttemptPayloadPrimaryProbe.ps1:52`.

This message does **not** prove a handle leak. That guard combines an incomplete
Bind-stage flag, open handles, and a still-current evaluation. It runs in the
analyzer's finally path after custody disposal, and can mask an earlier Bind
exception. The artifact does not record which condition fired or retain that
earlier error.

One source-level suspect is the fixture expectation: `canonical-fallback` is
excluded from `$validPair`, whereas `TryBindTransferPairForTest` checks original
custody/array/view identity and file currentness, not semantic route acceptance.
This is a hypothesis, not a measured binding outcome. Do not change that
expectation, runtime retirement, or acceptance based on the generic message.

Recommended next bounded step:

1. Preserve the original Bind error while still disposing the original custody.
2. Report bind-completion, open-handle count, and evaluation-currentness
   separately, without repeating authority/currentness evaluations for logging.
3. Add focused controls for an interrupted Bind and successful retirement, then
   rebuild and rerun this offline canonical case once to identify the first
   actual failure. Keep all current acceptance expectations unchanged meanwhile.

GUI PID 32600 exits 0 with complete capture, no timeout or deadline extension,
and 6,145.2125 ms active work. Native PIDs 38632 and 33680 report matching
268,435,456-byte fallback payloads. Both files independently match SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`;
exclusive read/write access succeeds, and all four processes are gone. This
confirms post-run release, not the independent in-process retirement assertion.
Source/retained/writer/semantic counts are 101,810 / 101,808 / 101,810 / 73,671.
The primary input hash is unchanged before/after:
`01f3a9575def3a8508be17ed866a96bbed90235c4de6b4e9c603d3665f9a9c95`.

Exactly one offline canonical was run (3 minutes 32 seconds). No live GUI/NKN
test, portable build, full matrix, commit, or push is included. Payload integrity or
EOF alone never grants route-cycle acceptance: the second coordinator-published
`file_tuna_v4` route remains required.

The starting checkpoint was
`artifacts/primary-origin-profile-20260927/FINAL-VERIFICATION.json`, SHA-256
`ac3dfdb0856a4d1e18619d1b06fa48b54038d771d2fd91615584aac0991b9ca1`.
All 7,225 starting pins matched before edits. HEAD remains
`a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`; the 428 starting dirty
entries are preserved.

The final dirty audit has 434 entries: three previously dirty files changed,
two previously clean files were edited, and three source/test files plus this
report were added. No starting paths were removed or changed status. The sealed
checkpoint and final hash audit are under
`artifacts/primary-origin-batch-20260927/FINAL-VERIFICATION.json` and
`FINAL-CHECK.json`.
