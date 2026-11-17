# Terminal collector capture timing alignment

2026-09-25, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Implements the separately reviewed scope in
`file-transfer-capture-timing-contract-review-20260925.md`, approved by the
user's subsequent instruction to proceed.

## Implemented boundary

The supplied-stream processor now associates its **original metadata stopwatch**
with the original stream capture before returning. The shared processor change
is exactly one call under both `NLINK_TEST_TERMINAL_REQUEST` and
`NLINK_FRONTIER_LEDGER`; this is a shared-source edit, not a production behavior
change. Ordinary and frontier-only compilations omit the call and test partial.

`Support/TerminalCaptureTiming.cs` privately retains that clock. Association is
single-assignment. Consumption checks original capture/result/source identity,
current source/output custody and a running original clock, then consumes one
attempt before invoking the unchanged native factory. Foreign or copied probes
cannot spend the original attempt. Neither success nor failure permits a second
factory attempt or a replacement clock. There is no public clock getter/setter,
clock-taking factory, pause/reset/restart operation, new source read or hash pass.
The consumer cannot supply replacement output metadata: it uses the capture's
original retained/writer/semantic metadata.

The original finalizer keeps its collector claim and processing one-shot gate.
It checks finalizer/collector ownership immediately before and after capture,
and retains its existing failure diagnostic and cleanup. Diagnostic timing
fields now describe the original metadata interval, not whole processing.
An unavailable/rejected timing association reports `-1`, not fabricated elapsed
time. The existing explicit post-processing expiry injection still uses the
same running processor clock.

The native factory's 250-ms comparison is unchanged. Metadata capture, retained
output sealing, result construction and return-to-consumer time all remain
charged. Earlier processing/hash/flush work is no longer additionally subject
to the fixture's former whole-processing 250-ms requirement. Its independent
5-second cooperative processing checks remain unchanged, as do semantic-copy,
output-ACK and completion encode/decode limits. No acceptance predicate,
coordinator/route authority, runtime transfer or throughput setting changed.

## Verification

New isolated timing/ownership tests compile the actual processor, not a fork,
on PowerShell 7 and Windows PowerShell. Eight cases per host cover pre-metadata
delay, inside-metadata delay, post-return expiry, ownership substitutions,
missing clock, cleanup, native-factory rejection followed by attempted retry,
and processing expiry. Timing controls use a delayed source read and a delayed
EOF-position observation already performed by the processor; no additional
production delay hook or timer replacement was introduced.

| Control | PowerShell 7 | Windows PowerShell |
| --- | --- | --- |
| 350-ms read delay before metadata | Processing 382 ms, metadata through capture 6 ms; accepted | Processing 394 ms, metadata through capture 6 ms; accepted |
| 350-ms delay after metadata starts, before processor return | Original metadata clock 351 ms; rejected | Original metadata clock 358 ms; rejected |
| 350-ms delay after processor return | Original metadata clock 364 ms; rejected | Original metadata clock 361 ms; rejected |
| 5,100-ms read delay | Processing 5,105 ms; `stream_source_not_current` | Processing 5,129 ms; `stream_source_not_current` |
| Ownership case | Seven pre-consumption probes plus retry/cleanup rejected; original succeeds | Same nine controls pass |

Every isolated case releases its three output files. These are processor unit
controls, not real finalizer/primary adoption or live transfer proof. The delay
inside processing distinguishes a carried timer from a fresh post-return timer.

Both real ordinary/frontier-only loaders were separately compiled on both
hosts: no terminal timing API is present, pathname native capture still works,
and supplied-stream processing still returns stream provenance without native
capture, with matching output hashes.

The two unchanged end-to-end collector expiry tests pass on both hosts. They
still reject before primary launch and prove child-live release of the original
collector and three outputs, missing semantic copy/completion, rejected retry,
no parent output adoption, zero primary evaluations and channel cleanup.

The directly affected collector regression lane passes all eight tests. It
covers both original finalizer hosts, successful retained
output adoption on both hosts, nonzero/failed-output rejection, and owned
writer-membership/primary consumption on both hosts.

Test records are under
`artifacts/capture-timing-alignment-20260925/results/`:

- `capture-timing-focused.trx`: initial four failures caused by a malformed new
  fixture that omitted `operational_log_writer_started`. The unchanged validator
  correctly rejected it. This record is retained, not reclassified.
- `capture-timing-focused-corrected-fixture.trx`: four tests pass after fixing
  only the fixture record; covers both timing hosts and both production loaders.
- `capture-timing-original-expiry.trx`: two original expiry tests pass.
- `capture-timing-collector-regression.trx`: eight directly affected tests pass.

The final runs total 14 passing tests. Including the initial malformed-fixture
run, all 18 attempts remain recorded: 14 passed and four failed. The original
end-to-end expiry diagnostics report 311/312 ms on PowerShell 7 and 317/318 ms
on Windows PowerShell (before/after native validation), both over 250 ms.

The test assembly was built once. Guarded support sources are compiled afresh
by each isolated PowerShell process, so the corrected fixture and finalizer
source are exercised by subsequent no-build runs. No aggregate lane, portable
build, live/network scenario or full matrix was run.

## Remaining proof and preservation

This is an approved fixture timing-scope change, not proof that either historical
intermittent capture failure had a timing cause or is fixed. Their original
failed records remain unchanged. The first unresolved primary edge remains
`file_writer_provenance_unproven`; full writer-copy provenance, terminal/payload
integrity joining and canonical route-cycle proof are not established here.
No live-256 readiness is claimed. The next bounded provenance step is to join
the existing owned membership with the original line origins under original
primary custody, without another authority evaluation.

All 8,503 previous pins matched before implementation. Only the two directly
affected previously dirty test helpers changed; the other 145 previously dirty
files are preserved. The processor was previously clean and received the three
guarded lines. Four test/support files and this document were added. The rebuilt
test DLL is the only other changed previous pin. Earlier evidence and the review
seal remain unchanged. No commit or push was performed.

The final verification record is
`artifacts/capture-timing-alignment-20260925/FINAL-VERIFICATION.json`.
