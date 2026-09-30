# Recovery-buffer-v1 implementation — 2026-09-27

## Scope and contract

The user's explicit approval of `recovery-buffer-v1` authorizes the bounded
validation-boundary change reviewed in
`file-transfer-recovery-check-cost-20260927.md`. That earlier review is retained
unchanged as historical evidence; this document records its implementation.

The functional change is confined to the recovery parser and its original
finalizer binding. Only the two line-processing checks use the existing
`FrontierSourceRead.BufferedProcessingWorkCurrent` rejection guard, and only
when the stream is the exact original recovery reader at its registry index.
The registry comparison selects provisional buffered computation, not custody
or authority. The guard rechecks original source/adopter identity, disposal,
processing state, lifetime cancellation and the original running work clock.

The one-callback scanner, arbitrary streams and wrappers retain full per-line
checks. All pre-existing full checks at read/decode/block/EOF boundaries,
pair/tuple/writer matching, marker construction/return and later handoff remain.
All four streams are scanned even when no markers are present. The 32,768-byte
buffer, strict decoder, framing, resource limits and marker interpretation are
unchanged. No time allowance, listener deadline, native-reader/adopter predicate,
coordinator behavior, transport behavior or route-acceptance rule changes.

This deliberately changes external-revocation detection timing: pure buffered
work can continue until the next full check, but cannot escape as accepted
facts. Transient mutations restored between full checks may be missed. This
is not equivalent per-line custody validation and gives no hard millisecond
latency guarantee. Cancellation/disposal and spent/stopped work still reject
through the local guard itself.

## Verification design

Test-only boundary hooks are excluded by the normal loader. New focused
controls run on Windows PowerShell and PowerShell 7. They cover:

- Original four-reader scans with and without markers; exact full-check counts.
- Generic streams, wrappers, copied source and copied recovery readers.
- Owner revocation, cancellation, spent budget and reader disposal before/after
  reads, at block completion, EOF and immediately before marker return.
- Local guard failure during a buffered line and after provisional marker
  accumulation; stopped-clock and source-disposal rejection.
- Deferred owner-revocation detection at the next full block check, with no
  returned markers/match, no revival and original handle release.

The maintained parser suite also asserts the generic path's exact per-line
full-check count, along with existing parsing, UTF-8, tuple and resource bounds.
The directly affected recovery-binding lane and successful native-handoff
controls are the regression gate before one offline canonical attempt.

## Results

All 142 focused controls passed, with no failures or skips:

- 70 new buffered-recovery boundary controls (35 per PowerShell host).
- Two existing parser suites, including the new generic full-check count.
- Two normal-loader exclusion controls.
- 66 existing recovery-marker/binding controls.
- Two successful original native-writer handoff controls.

The smoke assembly was rebuilt before testing. The three control TRX files
are in `artifacts/recovery-buffer-20260927/results`. No broad aggregate suite
or live GUI/network matrix was run. Source and test-assembly hashes were
recorded before the single targeted offline canonical attempt.

## Canonical result and remaining blocker

The single fresh offline canonical attempt is retained as **failed**, at:

`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-09b44c6ae3c64d028d35bc0078d73244`.

| Measured recovery capture | Previous attempt | This attempt |
| --- | ---: | ---: |
| Full recovery checks | 13,306 | 182 |
| Time inside those checks | 5,273.4303 ms | 70.6622 ms |
| Complete capture interval | 5,322.6808 ms | 116.0135 ms |
| Failed recovery checks | 0 | 0 |

The original cumulative work clock reached 4,545.2557 ms at coexistence and
4,568.2407 ms at cleanup, below its unchanged 10,000 ms allowance. The earlier
attempt reached 9,855.1241 / 9,874.4959 ms at those points. Four subsequent full
checks during recovery matching are preserved and add 2.0988 ms in this run.
These are elapsed-time observations from two different fresh attempts, not a
same-bytes benchmark or guaranteed performance bound. Native handoff was not
reached, so its remaining work-budget requirement is still unproven.

The first remaining failure is the original payload offer: its wrapper
listener expired after 100,006 ms before connection, with
`lifetime_cancelled=False`, `work_ms=0`, `work_running=False`. The GUI's later
offer failed to connect after 10,016 ms. This is the existing independent
listener-lifetime failure, not exhaustion of the GUI work allowance. It was
not bypassed or fixed, and the attempt was not repeated.

Both apps completed 268,435,456 bytes on `post_tuna_fallback_v6`, with matching
session/transfer and SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
Independent payload hashes agree and exclusive access succeeds after cleanup.
GUI 36960 / sender 53720 / receiver 20404 have exited; the GUI exit is 1 and
`timedOut=false`. Source/retained/writer/semantic line counts are
101,827 / 101,825 / 101,827 / 73,686; the source is 92,061,056 bytes.

No native-writer handoff or primary analysis completed. Fallback completion
and matching SHA do not satisfy the live route-cycle contract or establish a
second coordinator-published `file_tuna_v4`.

## Recovery checkpoint and next step

Branch/HEAD remain `v0.7.0` /
`a653d4063070235c71fe211480aeb37863d9b2a4`. Before edits, all 2,890 prior pins
matched and there were 398 dirty entries. The existing unrelated work is
preserved; only five previously dirty source/test files changed, plus three
previously clean tracked files, two new test files and this report. There are
404 dirty entries. No commit or push was made.

Detailed observations and hashes are in
`artifacts/recovery-buffer-20260927/observations.json`; the cumulative checkpoint
is `artifacts/recovery-buffer-20260927/FINAL-VERIFICATION.json`. The previous
checkpoint is retained unchanged at
`artifacts/recovery-check-cost-20260927/FINAL-VERIFICATION.json`, SHA-256
`743a0dd6594c24896f5dfae6ba62bf5531376913d22aba09d78b2f4b813e4737`.

The approved recovery-buffer implementation is complete. Next is a separately
scoped listener-lifetime fix plan, preserving cancellation/exit handling,
connected-exchange limits, the wrapper work allowance and route acceptance.
That correction is not included in this approval. No live NKN run, portable
rebuild or full GUI matrix occurred, and live validation is not ready yet.
