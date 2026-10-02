# Native worker source read timeout and diagnostic plan

2026-10-02. The fresh offline scale fixture localizes the first unfinished
worker operation to `Read-FileTransferLogEvents` using the original owned
`SourceReader`. Native attachment and source opening return promptly. The
30-second parent barrier still fails; the underlying operation or predicate
inside the combined source-read/parser call remains unknown. No production
code or limit is changed in this step.

## Diagnostic implementation and controls

The content-input worker fixture now writes at most 32 ordered stage records
around its existing operations. Each record contains only a stage name,
sequence number, diagnostic flag, and elapsed time from a separate observation
stopwatch. It reads no source, token, identity, or custody properties and
provides no authority or work allowance.

The fixture parent retains stdout and stderr after its existing release,
five-second exit wait, and conditional termination. Saved output is capped at
65,536 UTF-16 characters per stream, with original lengths and truncation
status recorded separately. This bounds artifact retention, not the existing
in-memory `ReadToEndAsync` tasks. Incomplete or faulted tasks are recorded
without waiting for or retrieving their results. Capture failures are warnings
and cannot replace the original worker outcome. Other worker families do not
use this capture path.

Nineteen focused cases pass: two output-bound controls, eleven original
native-input identity and rejection controls, two deliberately stalled worker
controls, and four compatibility controls for the other native worker family.
Both PowerShell hosts preserve the timeout error and retain the controlled
worker's error and cleanup-return record. The deliberately stalled fixtures
remain failures; the tests pass because that unchanged failure is expected.
The original parent checks for closing its retained pins remain intact.
Two additional standalone controls exercise the actual capture catch block
with an invalid output directory and stop-on-warning behavior. Both retain
the original worker failure instead of replacing it with a diagnostic error.

No extra authority evaluation, source pass, custody probe, changed wait API,
changed synchronization order, or clock reset is added. The shared adoption
cap remains 15,000 ms, the owned cursor parser cap 10,000 ms, and the native
worker producer cap 20,000 ms. The signaling barrier remains 30,000 ms.

## Fresh scale evidence and limits

One normal-path scale fixture runs and fails with the original
`Native worker attachment deadline`. Its whole original ledger-processing
call succeeds in 5,243 ms, with cumulative adoption work at 7,186 ms. The
original semantic-copy call returns all 208,326,024 bytes at 9,718 ms
cumulative work. Persisted semantic and analyzer copies both contain 102,218
lines and have matching metadata and actual SHA-256 values. Those facts prove
the earlier copy gate in this run, not continuing custody or live acceptance.

The retained worker trace contains nine consecutive records. Loading returns
at 969 ms; the begin wait returns at 977 ms. Attachment returns at 1,024 ms,
47 ms after its start. The existing owned-input lookup and source-open call
return at 1,028 ms, three milliseconds after their start. These are diagnostic
wall-clock intervals, not producer active-work accounting or throughput.

`source_read_started` is the final record, also at 1,028 ms. There is no
`source_read_returned`, later materialization/evaluation/content-selection
record, or captured stderr exception before the existing timeout cleanup
finishes. Both output tasks complete and neither retained stream is truncated.
The first missing proof edge is therefore the source-read/parser return, not
native attachment or content selection. The artifact does not identify an
inner rejection, a particular blocked native check, the row reached, or an
exhausted active-work allowance. Empty stderr is not proof of no internal
failure, and absent cleanup markers do not identify the worker exit cause.

Code inspection shows that this call combines owned native line enumeration,
line conversion, and event-array construction. The original native reader
also performs its resource and continuation checks before publishing each
line. The current trace cannot separate their costs or distinguish slow
progress from a blocked operation. No runtime fix or another cap increase is
justified by this evidence alone.

## Next bounded diagnostic and fix criteria

Next, instrument only this unfinished call in a test profile. Reuse the
existing passive native-read counters and first-rejection observations; add
bounded observations of enumeration progress and parser conversion if needed.
Read already-observed fields rather than calling another currentness or
authority check. Preserve every original publication, budget, cancellation,
identity, and retirement check, including their short-circuit order.

Verify the small stale-owner, mutation, cancellation, and expired-work
controls before one targeted diagnostic scale run. Keep the 30-second barrier
and all work limits unchanged. A fix plan must name the first observed inner
failure or dominant original operation, explain why the change preserves
authority and rejection behavior, and include directly affected regressions.
Do not optimize both parsing and native validation speculatively or replace
managed waits based on this outer timeout.

The [verification receipt](../artifacts/native-worker-stage-rca-20261002/verification.json)
pins this implementation, tests, retained scale output, and preserved prior
evidence. The acquisition gate and production integration remain incomplete.
Historical live verdicts are unchanged. No portable rebuild, live GUI/NKN
run, full matrix, commit, or push occurs in this step.
