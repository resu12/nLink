# Native source and parser timing RCA and bounded fix plan

2026-10-02. The fresh offline scale profile shows the owned source-read loop
making steady progress through the existing parent deadline. It has not
reached EOF. Parser conversion is the largest measured component; native
reading also has substantial cost. No inner rejection is observed at the
retained samples, and charged native work is below its 20-second allowance.
This step adds diagnostics only. The scale gate remains failed, and all
limits, waits, route authority, and acceptance rules remain unchanged.

## Diagnostic scope and verification

Only the separate worker test profile enables the existing native-read
diagnostic partials and counters. A new passive formatter reads those fields
without evaluating currentness, moving a handle, or changing a work clock.
The existing source-read hook supplies sparse progress samples. Each worker
writes at most 32 profile records, in addition to the bounded stage records
and output retention added in the preceding step. No monitoring thread,
additional source pass, authority evaluation, or currentness cache is added.

The worker instruments its own copy of `Read-FileTransferLogEvents`, around
the original owned-line assignment, log-line conversion, and final event-array
return. Each original statement and its order are preserved. Exactly one
occurrence of every insertion anchor is required; removing the inserted
observations must reproduce the exact original function body. The production
parser file is unchanged. The normal worker loader does not enable this
profile, and the profile helper is absent from normal assemblies.

Eighteen selected tests pass: five new profile controls, four existing
projection and passive-counter contract checks, seven native source controls,
and two unprofiled input controls. The native controls cover valid reads,
expired active work, cancellation, changed scope and facts, and a copied
reader. The profile controls retain the existing expired-work, stale-owner,
and mutation rejections. Neither diagnostic observations nor successful input
identity fixtures grant content coverage or route authority.

## Fresh scale observations

One `scale-profile` fixture runs and fails at the unchanged
`Native worker attachment deadline`. Earlier profiled processing succeeds,
with the cursor parse taking 5,819 ms and cumulative adoption work at 8,443 ms.
The original semantic-copy call returns 208,326,025 bytes at 11,238 ms
cumulative work. The persisted semantic and analyzer files have matching
byte counts, line counts, and actual hashes. These facts establish the earlier
copy gate only, not continued custody or overall acceptance.

The retained worker output contains nine stage records and twenty profile
records. Both output tasks complete, stderr is empty, and neither stream is
truncated. The owned-line assignment returns before enumeration starts;
subsequent native reads and parser conversions interleave. The samples show
both operations returning at records 1, 256, 4,096, 16,384, 32,768, and 49,152.
At the final sample, 49,152 conversions have returned out of 102,218 expected
semantic records, and `source_ended` is false.

| Observation at the final retained sample | Seconds |
| --- | ---: |
| Elapsed source and parser loop interval | 30.160598 |
| Cumulative parser conversion intervals | 14.752128 |
| Cumulative native read intervals | 9.545272 |
| Original charged native work | 9.583283 |
| Mapped file reads within native reads | 0.132151 |

The four original native predicate families account for about 8.297 seconds
within the native-read interval: base checks 1.583, scope checks 0.672, file
checks 3.819, and pin checks 2.223. These nested intervals must not be added to
native reads as independent costs. The loop interval also includes PowerShell
iteration, dispatch, event accumulation, and diagnostic overhead. It is not
live transfer throughput or an unprofiled performance benchmark.

The final sampled native work is about 9.58 seconds against the unchanged
20-second producer cap. Its recorded rejection remains `none`, its close
state `open`, and its exception `none`. Those observations apply to that
sample, not to unseen work afterward. There is no source-return, EOF,
evaluation, content-selection, or final-cleanup record before termination.
The exact last row and worker exit cause are not established.

## RCA and proposed next change

The observed parent timeout preempts a still-progressing read and conversion
loop. It is not evidence of a stuck attachment, failed mapped read, expired
producer allowance, or one identified failing native predicate. The first
missing proof edge remains original source EOF and the parser return. Parser
conversion is the largest separately measured component, but these data do
not justify removing native validation or redesigning both components.

A linear extrapolation from the final sample gives about 62.7 seconds for the
full loop on this diagnostic run. This is an estimate, not a completion bound
or acceptance proof; instrumentation and host variation affect it. Native
active work may approach its real allowance before completion. A longer
parent wait must not hide that eventual rejection or turn a timeout artifact
into success.

Recommended next step: review and apply a 90-second attachment-signaling wait
only for the two exact offline scale fixture cases. Keep every other worker
at 30 seconds. Retain the native producer cap of 20 seconds, the shared
adoption cap of 15 seconds, the local cursor parser cap of 10 seconds, all
record and byte limits, the existing exit and cleanup waits, and every
authority, cancellation, identity, publication, and retirement check.
Diagnostic metadata must report the actual selected fixture barrier.

Before that change, verify exact scenario selection and ordinary deadline
rejection with focused controls. Afterward, run one normal scale fixture,
without the added timing profile. Require original EOF, input checks, and
worker completion; otherwise preserve the failure and diagnose its new
first edge. Do not raise active-work limits, optimize the parser, or replace
managed waits in the same change. The proposed wait adjustment is not
implemented or accepted in this step.

The [verification receipt](../artifacts/native-content-read-profile-20261002/verification.json)
pins this profile, its test results and observations, the launch source
checkpoint, and preserved historical evidence. Production integration and
content acquisition remain incomplete. No live GUI/NKN run, portable rebuild,
full matrix, commit, or push occurs.
