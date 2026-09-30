# Capture timing contract review

2026-09-25, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Continues `file-transfer-collector-expiry-20260925.md`.

**Recommendation:** align the terminal collector fixture with the existing
production native-metadata capture interval by retaining the processor's
original stopwatch. Do not start a fresh timer after processing returns.
This is a proposed contract change, not an implemented or approved change.
Current code, timing scopes/limits and acceptance remain unchanged.

## Evidence and exact boundaries

The source references below are pinned in this review's verification record.
The comparison is with the production pathname/native-capture branch of
`GuiSmokeLogLedgerStreamingProcessor`, not a claim that every production or
frontier path has one universal timeout.

| Budget | Actual start and checks | What it covers |
| --- | --- | --- |
| Current terminal fixture native capture: 250 ms | `Support/TerminalCollectorFinalizer.cs:111` starts before the processor; the same clock reaches `CaptureNativeOutputs` at line 125 | Stream processing, hashing, durable flushes, metadata capture, output retention/result construction, return to the finalizer, and native capture validation |
| Production pathname native capture: 250 ms | `tools/GuiSmokeLogLedgerStreamingProcessor.cs:1111` starts after flushes, before the first native output metadata capture; the same local clock reaches the factory at line 1162 | Output metadata capture and subsequent result construction through native capture validation; excludes earlier stream processing/hash/flush work |
| Supplied-stream processing: 5,000 ms | `GuiSmokeLogLedgerStreamRead` starts its private clock at line 370; checks during reading/origin recording and at lines 1152/1157 before processor return | Cooperative elapsed-time checks across supplied-stream processing, including final post-flush checks; not an interruptible I/O deadline or a lifetime timer for the returned capture |
| Writer completion encode/decode: 250 ms each | `tools/FileTransferOps/LocalGuiWriterRequest.cs:236` and line 364 | Separate completion publication and decoding intervals; neither is the native-capture clock |

The native factory rejects elapsed time **greater than** 250 ms at entry,
during writer validation and immediately before result creation
(`GuiSmokeLogLedgerStreamingProcessor.cs:599`, `:614`, `:619`). Exactly 250 ms
is not rejected by those comparisons. Preserve these existing comparisons;
do not replace them with a different threshold or a new total-finalizer budget.

The production clock starts before all three
`GuiSmokeLedgerCapturedOutput.Capture` calls. The supplied-stream branch also
creates that local `captureClock`, but currently returns its stream capture
without carrying the clock out. It takes the branch at line 1150; only the
pathname branch subsequently calls the native factory at line 1162.

Consequently, moving `Stopwatch.StartNew()` in the test finalizer to immediately
after `ProcessFromStream` returns is **not equivalent** to production. It would
exclude metadata capture, retained-output sealing, result construction and
processing-to-consumer time from the 250-ms interval. It would also discard the
processor's real clock rather than preserve its original boundary.

The 5-second processing clock is checked after flushes, but cannot interrupt a
blocking synchronous `Read` or `Flush(true)`. A slow operation can complete and
then be rejected. `GuiSmokeLedgerStreamCapture.IsCurrent` at lines 61-75 checks
identity/liveness/position/output custody, not that 5-second clock. It would be
incorrect to describe this as an automatically expiring 5-second capture token.
The existing parent/child hard-deadline mechanism remains a separate safeguard.

Native capture is optional in the ordinary pathname processor: exceptions are
caught and no native capture need be issued. The terminal fixture deliberately
requires that capture for its provenance test and throws when it is missing.
Do not change production optional-capture behavior to match the stricter test.

## Decision and tradeoff

Keeping the present whole-processing 250-ms scope is valid if that stricter
latency requirement is intentional. It can reject completely processed input
because of disk or scheduling delay before metadata capture; a passing transfer
or matching payload hash cannot override the rejection.

The recommended alignment removes that extra test-only latency requirement
while keeping the original metadata budget and independent processing bounds.
This **does change which fresh fixture attempts may pass**, so it must be
approved as a contract change. It is not a cosmetic timer refactor and is not
evidence that either historical intermittent failure is fixed.

The controlled expiry tests proved the clock-expiry failure mechanism and
cleanup, not the historical cause. Their after-processing 300-ms delay must
still fail after alignment: the original metadata clock has already started
and must continue running through the caller's native-capture attempt.

## Proposed bounded implementation contract

1. Carry the exact `captureClock` created at the original metadata boundary into
   the same original stream-capture lifecycle before the processor exposes its
   result. No replacement clock, elapsed-time reconstruction, serialized timer,
   path reopen, second processing pass or second hash pass is permitted.
2. Keep that clock private and running. Consumers receive no mutable stopwatch,
   setter, reset, restart or pause API. Diagnostic elapsed values are evidence
   only and cannot be fed back to establish eligibility.
3. The clock association is single-assignment to the original processor/result/
   stream capture. A copied result or capture, missing association, wrong source,
   wrong finalizer, retired owner or substituted output must not be accepted.
   No new public clock-taking capture factory is introduced.
4. The original finalizer retains its exact collector claim and
   `collectorAttempted` gate. A guarded capture-consumption operation uses the
   associated clock with the unchanged factory and original immutable output
   metadata. Its own valid attempt is consumed on success or failure. Repeated
   calls cannot invoke the factory again or replace a failed attempt's clock.
   Invalid foreign/copy probes must not consume the original owner's attempt.
5. Check original finalizer/collector/result/stream ownership before and after
   capture. Keep EOF/length/hash facts, writer sequence/scope validation, origin
   bounds, output custody and cleanup. Time eligibility never grants route,
   runtime, primary, publication, repair or completion authority.
6. Leave native capture at 250 ms, supplied-stream checks at 5 seconds,
   semantic-copy and output-ACK intervals at their existing 5 seconds, completion
   encode/decode at 250 ms, and primary cumulative work at its existing limit.
   Do not introduce a new combined budget or broaden other intervals.
7. Failure still withholds capture/completion/output adoption, preserves the
   bounded diagnostic, prevents primary launch and releases the original files
   and channels. Successful child exit and original parent adoption remain
   required. Never retry expired evidence with a fresh clock.

## Source-change boundary

The original metadata stopwatch is currently local to `ProcessCore`; the
finalizer cannot recover it from the returned result. Therefore a faithful
alignment is not achievable solely by moving a timer in the finalizer.

The smallest proposed integration is a guarded association call in
`tools/GuiSmokeLogLedgerStreamingProcessor.cs`, plus a guarded test partial on
the already-partial `GuiSmokeLedgerStreamCapture` that privately retains and
consumes the original timing association. Invoke the association only on the
supplied-stream path, before result return. The existing terminal test loader
already compiles with `NLINK_FRONTIER_LEDGER` and `NLINK_TEST_TERMINAL_REQUEST`;
the new seam must require both symbols. The ordinary namespace loader and
production frontier-only loader must keep their existing behavior and public
API, with the terminal timing methods absent.

This proposal would edit a shared processor source file under a test-only
compile guard. That exception to the previous test-files-only boundary needs
explicit inclusion in implementation approval. Do not claim the shared source
file is unchanged merely because its production-compiled behavior is unchanged.
Do not rewrite a private processor fork or broadly transform its algorithm in
the loader to avoid acknowledging this small shared-source change.

Finalizer consumption, diagnostic field meanings, the expiry fixture and the
test loader may change only as directly required by this association. The
normal factory, runtime transfer/coordinator code, acceptance predicates,
canonical scenario contract, throughput settings and unrelated UI are out of
scope. The review does not prescribe a general timing framework or a new
production feature. If implementation needs broader changes, stop for review.

## Required verification before considering alignment complete

- On both PowerShell hosts, a controlled delay above 250 ms but comfortably
  below 5 seconds **inside processing and before metadata clock creation** can
  complete when all actual metadata-phase work fits its unchanged budget.
  Record both phase durations; a delay outside processing does not prove this.
- A delay after metadata clock creation but before native capture rejects the
  attempt. Preserve both existing end-to-end expiry tests and their child-live
  file-release, missing-copy, retry, parent-adoption and channel checks.
- Delay inside metadata capture/result construction is also charged to the
  same 250-ms interval. This must fail; a timer started after processor return
  would incorrectly pass this distinguishing control.
- A supplied-stream operation that exceeds the existing 5-second processing
  checks still fails with no accepted completion or primary launch. Do not
  describe this as preemptive cancellation of synchronous I/O.
- Copied/foreign timing ownership, absent association and repeated consumption
  reject without a new factory attempt. Successful and failed native capture
  cannot be consumed again or renewed with a fresh clock. Cleanup invalidates
  the association along with the original stream/output owner.
- Compile and check the isolated terminal types on PowerShell 7 and Windows
  PowerShell. Separately verify the unguarded ordinary and frontier-only loaders
  expose no new terminal timing API and preserve their existing behavior.
- Run only the new focused timing/ownership cases first, then one directly
  affected collector finalizer/output regression lane after stabilization.
  Reuse relevant original-primary/membership checks where the changed path is
  exercised. No full matrix, aggregate rerun loop or live-256 test is required
  to validate this bounded test-only timing change.

The new pre-metadata positive outcome must be described as behavior under the
newly approved timing scope, never a reclassification of earlier failures.
Full writer-copy provenance, primary handoff, terminal/payload-integrity join
and canonical route-cycle proof remain separate obligations.

## Current disposition and preservation

Review complete; implementation is recommended **subject to separate approval
of this exact timing scope and guarded shared-source seam**. Until then, retain
the existing stricter fixture. No code, test, timing or acceptance change was
made during this review, and no new success evidence is claimed.

All 8,498 preceding file pins matched. All 146 previously dirty workspace files
remain byte-for-byte unchanged; this review is the only added workspace file.
The verification record is
`artifacts/capture-timing-review-20260925/FINAL-VERIFICATION.json`.
No tests, builds, live/network runs, full matrix, commits or pushes were run.
