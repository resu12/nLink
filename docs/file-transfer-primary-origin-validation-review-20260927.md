# Post-EOF origin validation: measurement and contract review

Date: 2026-09-27. Status: 17 focused checks pass. One rebuilt offline canonical
run isolates the original post-EOF budget failure. The batched-origin contract
below is a proposal, not implemented or approved. Default runtime validation
and the cumulative 20,000 ms limit remain unchanged.

## Scope and first edge

The preceding canonical run read all 73,726 lines, then exhausted the same
cumulative work budget inside `SourceOrigin`. `merged-native` calls the scalar
origin API for every parsed event after EOF, not just selected failure witnesses.
The first 12,945 origins were positive; the remaining 60,781 were zero after
retirement. Both relay and milestone-compatibility consumers assert the complete
origin vector. Dropping that assertion or sampling origins is not an acceptable
fix.

The original scalar method verifies native reader identity before starting work,
then checks full live resource currentness before examining EOF, ordinal bounds,
the exact emitted string reference, and its physical origin. A currentness
failure closes the original input; a foreign string or invalid ordinal merely
returns zero. These semantics remain unchanged in this step.

## Measurements and rejected candidate

A codec-free mapped-channel control uses the actual 1,928,659-byte facts size
and all 73,726 post-EOF origin requests. These are transport-component timings,
not an authority evaluation or a replacement for the native origin method.

| Component | Elapsed |
| --- | ---: |
| Current full facts validation | 4.4099 s |
| Current guarded body comparison | 3.8632 s |
| Header alone | 0.0524 s |
| Fixed file offer without nested facts | 0.7037 s |
| Candidate UCRT body comparison | 3.9122 s |

The UCRT candidate was not faster, even though its control held lifetime pins
around the measurement loop. It was not adopted. The component-control source
was restored byte-for-byte to its previous sealed version. Production still
uses the previously tested `msvcrt.dll` comparator.

## Diagnostic changes and verification

Only the explicit test profile records origin-entry cumulative work, total
origin work/calls, positive results, and the four existing currentness predicate
costs within that phase. Each original predicate executes once; origin results
are recorded from the original expression, not recomputed. The default source
projection is unchanged. Original timer start/stop boundaries, lock order,
short-circuit ordering, rejection and cleanup are retained.

Counters add measurement overhead and elapsed times include scheduling effects.
No original work is moved outside its budget and no overhead is subtracted to
claim success. Origin-phase counters are a subset of active-other predicate
counters, not an additional authority evaluation.

Focused result:
`artifacts/primary-origin-profile-20260927/results/origin-profile.trx`.
All 17 checks pass on the first run in 1 minute 42 seconds. This includes both
unchanged default source projections, original short-circuit/clock contracts,
11 native source controls (including origin budget, equal-but-substituted line,
invalid ordinals and copied reader), and two default-loader isolation checks.
The controls verify exact attempted/positive origin counts, no duplicated
predicate calls, first-close preservation, and side-effect-free formatting.
Smoke rebuild: zero warnings/errors. Changed PowerShell source parses and the
focused diff check passes.

## Proposed bounded contract: approval required

Recommended review direction: add an atomic, bounded batch API for post-EOF
origin evidence, leaving the existing scalar API unchanged.

1. Process every requested origin, in caller order, in batches of at most 64.
   This is batching, not selection: the canonical vector still contains all
   source-event origins. The separate failure-witness selection cap is unchanged.
2. Admit only the original native reader with genuine EOF and current original
   custody. Use the same owner/inputs locks and original cumulative work clock.
3. Validate full resource currentness at batch entry and before publishing its
   result. Before each tuple, check the original closed flag, cumulative limit,
   owner/base liveness, scope currentness, and file/facts cancellation signals.
   These cheaper checks may abort only; they never authorize output or replace
   the closing full facts/pin check. Check the cumulative limit again after that
   closing full check, before output is exposed. Never cache a successful
   resource verdict between batches.
4. For every entry, retain the original one-based bounds check and exact emitted
   string-reference comparison, and derive the physical origin from the retained
   native facts. Never reconstruct identity from equal text, a path or a hash.
5. Hold results privately until the entire batch and its closing guards pass.
   Return all results or none; a failing batch must not publish a successful
   prefix or fabricate zero/positive entries. Resource retirement still closes
   the original input. Invalid request data must not acquire authority.
6. Return evidence data only, not a new ownership token or route/proof permission.
   Keep the 20,000 ms limit, complete input/origin assertions, analyzer counts,
   coordinator authority, and all live route-acceptance rules unchanged.

The material tradeoff is explicit: full facts/pin scans move from every scalar
origin to the boundaries of a bounded batch. Per-entry identity validation is
retained, and no partial batch becomes visible. This changes validation cadence
and therefore requires separate approval before implementation. A batch API
that still rescans the entire facts body for every entry would not address the
measured repeated-scan cost.

This is not continuous byte verification: a transient facts/pin mutation that
is fully restored between boundary scans could be observed less often than
with the original per-origin scans. The closing guard prevents publication
while a detectable mismatch remains; it does not prove that no transient
mutation occurred. Approval must explicitly accept this bounded validation
cadence, not just the performance goal.

Reject null, empty, mismatched-length or over-64 request arrays without output.
For a current reader, an invalid tuple returns no batch result but does not
retire the reader, matching the scalar API's invalid-data behavior. Resource,
cancellation or budget failure retires it through the original close path.
Read each ordinal/string tuple once into bounded local state and validate those
exact values; never validate an array slot and then reread it for publication.
Valid repeated or out-of-order requests retain scalar mapping semantics and
caller order; the canonical consumer still asserts its complete ordered vector.
Expose no mutable native ownership or retained facts buffer. The original
reader identity check remains before any native resource evaluation.

If approved, implement only this origin-evidence path and its existing fixture
consumer. Verify batches of 1, 63, 64, and 65 entries (65 must split or reject at
the API boundary), all-origin ordering, true EOF, copied reader, substituted
string, wrong ordinal, and invalid batch shapes. Inject scope/facts/pin changes,
parent cancellation, disposal and budget expiry before, during and after a
batch; require no partial output and correct cleanup. Retain scalar regressions.
Then rebuild and perform one targeted offline canonical verification before
considering any live run. Approval is not a promise that later proof edges pass.

## Canonical observation and preservation

Artifact:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-2f2bf26d8b274bdca5d1395d646cb9b0`.

The original native handoff succeeds and repeated take is rejected. The primary
reads all 73,731 lines (69,678,433 code units) and observes genuine EOF. Its
attach/open/read work totals 18.735489 seconds exactly, leaving 1.264511 seconds
of the original budget on entry to the first post-EOF origin request.

| Original origin phase | Calls | Work |
| --- | ---: | ---: |
| Base liveness | 11,082 | 0.0609207 s |
| Scope currentness | 11,082 | 0.0315341 s |
| Files/facts currentness | 11,082 | 0.7632078 s |
| Native pins | 11,082 | 0.4061091 s |
| Other origin work, including failed/closed calls | — | 0.0087879 s |
| Total origin work | 73,731 requests | 1.2705596 s |

The phase totals reconcile exactly to the final cumulative 20.0060486 seconds.
There are 11,082 positive origins, with the first zero at ordinal 11,083 and
62,649 zeros in total. Successful original predicate counts exactly match
positive origins. The budget check refuses before evaluating those predicates
on the failing request; later requests remain closed. First rejection is
`resource:budget`, first close `SourceOrigin`, at 20,001 ms with EOF still true.

This remains a failed integration: two analyzer evaluations, one native parser
invocation, no complete proof artifacts, and no coordinator parity result.
The later payload-catalog error follows original input retirement. The measured
facts body is 1,928,789 bytes (130 bytes larger than the preceding run), with
no acknowledgment channel on the primary facts object. Do not interpret
different origin-prefix lengths across runs as a correctness change or a
precise instrumentation-overhead estimate; source counts and scheduling differ.

Exactly one rebuilt canonical run was performed (3 minutes 45 seconds). No
live GUI/NKN test, portable rebuild, full matrix, commit, push, runtime
optimization or contract implementation is included here.

Primary PID 48456 exits 1 without timeout. GUI PID 59056 exits 0 with complete
capture and no deadline extension, using 6,707.368 ms of its 10,000 ms work
allowance. Native PIDs 40844 and 8336 report matching 268,435,456-byte payloads
on `post_tuna_fallback_v6`. Both files independently match SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`,
exclusive read/write access succeeds, and all four processes are gone.
Source/retained/writer/semantic counts are 101,879 / 101,877 / 101,879 / 73,731.
The original primary input hash remains unchanged:
`087dbdcec07e0ab15c907e9ff70fb871e6194338fb02ed3ed0dd27c3e69b6e41`.
Neither EOF nor payload integrity grants live route acceptance; the second
coordinator-published `file_tuna_v4` is still required.

Starting checkpoint:
`artifacts/primary-facts-exact-20260927/FINAL-VERIFICATION.json`, SHA-256
`6f66888ba08fa8c1f9a78b3b1d2561be894a25a031fff3310f80fbd76a965374`.
The 7,060-pin preflight found only the concurrently edited comparator-control
import; reversing that one test edit reconstructed its old hash and the file
was then restored exactly. No unrelated change was detected. HEAD remains
`a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`, with 427 starting dirty
entries preserved.
The final dirty audit has 428 entries: only four intended profiling/test files
changed and this review was added; no paths were removed or statuses changed.
The rebuilt smoke assembly is the only additional changed prior pin.
Observations and the preservation checkpoint are under
`artifacts/primary-origin-profile-20260927/observations.json`,
`FINAL-VERIFICATION.json`, and `FINAL-CHECK.json`.
