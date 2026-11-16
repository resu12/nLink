# Review: bounded native-ledger validation cadence

2026-09-26, `v0.7.0`, HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`.
Continues `file-transfer-devlocal-metadata-queue-20260926.md`.

**Status: proposal for separate approval; review only.** No source, tests,
validation frequency, budget, or acceptance rule is changed in this step.

## Decision

Recommend a native-reader-only buffered-processing path, conditional on the
safeguards and tests below. Keep full ownership checks at admission, bounded
input/refill boundaries, and output retention/return boundaries. While parsing
already-read bytes, retain cheap checks of the original processing identity,
phase, cancellation, and the same cumulative five-second work clock.

This is not equivalent sampling frequency: an ownership change that is not
already reflected by local retirement/cancellation can be detected later,
at the next mandatory boundary rather than the next record. Parsing and
temporary writes in that interval must remain tentative. A transient external
change restored between full checks might not be observed; neither the old
polling implementation nor this proposal proves uninterrupted ownership.
Approval must cover this detection-granularity tradeoff. If per-record full
ownership revalidation is a required contract, reject this proposal and retain
the current cadence. No timeout extension is recommended.

## Evidence and current contract

The latest rebuilt offline PowerShell 7 run is
`artifacts/frontier-cross-process-receive-20260926/pwsh-writer-source-read-cross-process-valid-9043d3e65e244b948e85673b9ce5220c/`.
It completes 268435456-byte transfer/native-hash assertions, then fails original
log processing at 5002 ms for 40814298 bytes and 54648 lines. The end-to-end
test remains failed. The metadata queue fix is not this remaining failure.

`artifacts/owned-source-validation-20260926/CHECK-COUNT-DIAGNOSTIC.json`
records 275734 outer `CanRead` calls for 1248 physical reads of a prior,
size-matched log. Its 501 ms generic replay omits native custody and is not an
acceptance result. Those counts exclude nested native checks within an actual
owned read; the proposal does not imply only 1248 total ownership checks or
guarantee the full pipeline will fit in five seconds.

Relevant current implementation:

- `tools/GuiSmokeLogLedgerStreamingProcessor.cs:391`: `CheckCurrent` combines
  a processor-local five-second clock with `Source.CanRead`. It is called
  by line reads, buffered fills, and every recorded origin.
- `tools/FileTransferOps/LocalGuiWriterFrontierSource.cs:41`: the exact native
  reader's `CanRead` traverses full original binding ownership. `Read` at line
  82 checks it before and after the native adoption read.
- `tools/FileTransferOps/LocalFrontierFileSeal.cs:52`: native reads retain
  original reader identity, position, source verification, and a 32768-byte
  maximum. The seal at line 199 holds the original file read-only with write
  and delete sharing denied; no pathname reopen is needed for processing.
- `tools/FileTransferOps/LocalFrontierFileAdoption.cs:151`: the adopter's work
  clock is cumulative, covering earlier control/adoption/seal work as well as
  processing. The processor's newer clock is not a replacement for it.
- `tools/GuiSmokeLogLedgerStreamingProcessor.cs:1122`: retained outputs are
  duplicated/sealed before the final stream-capture checks at lines 1152 and
  1157. Failure cleanup closes the retained lease at line 1172. Optional
  provenance capture has its own exception-swallowing catch; mandatory
  validity checks must not be placed inside it.

## Proposed contract

### Scope and admission

Only the exact original `LocalGuiWriterRequest.FrontierSourceRead` in the
`NLINK_FRONTIER_LEDGER` compilation may use the reduced buffered checks.
Require full original currentness, the existing one-shot reader identity,
zero starting position, and an already-active original processing interval
before enabling it. A clone, wrapper, generic stream, inactive/finished reader,
caller flag, arbitrary callback, type-name string, or caller-supplied lifetime
token cannot select or authorize this path.

Prefer internal exact-type selection within the existing processor, not a new
public bypass API. Preserve the ordinary standalone and generic stream paths'
current checks. `FrontierSourceRead.CanRead`, `IsCurrent`, `Read`, source
sealing, and original writer/cursor matching remain full checks. The existing
generic `ProcessOwnedCursor` signature alone is not proof of native ownership.

### Two distinct checks

The existing full check remains the only source/ownership validation. Add an
internal rejection-only processing check for buffered work. It must use the
original source/adopter identities, the exact registered reader, active
one-shot processing state, local retirement/cancellation, and the original
shared work clock under its existing synchronization. Also retain the
processor-local limit. Never stop, reset, replace, or renew either clock.

This local check may permit only further tentative CPU/output work; it cannot
prove native ownership, issue a receipt, approve capture, or revive a retired
reader. Capture the original cancellation signal as a negative signal only;
the caller's output-lifetime token is not authority. Do not implement the
cheap check by calling the existing `ProcessingWorkActive` property, which
itself performs full ownership traversal and would retain the bottleneck.

On failure, abort and use the existing original-owner retirement path. Reject
copies without retiring someone else's original. Do not enter GUI/finalizer
locks from inside adopter/output locks, and keep cancellation callbacks
limited to resource cleanup.

### Mandatory boundaries

| Boundary | Required check |
| --- | --- |
| Admission and initial source facts | Full original currentness and processing identity/phase |
| Before/after each native source read | Existing full checks, including 32768-byte read cap; unchanged |
| Before/after refilling the 32768-character parser buffer | Full currentness, even if the underlying StreamReader serves its own buffered bytes |
| Buffered line scanning, line completion, origin insertion | Original cumulative/local deadline, phase, identity, and cancellation rejection checks |
| LF cursor input refill | Full boundary checks around its 32768-byte reads; no bypass for generic cursor inputs |
| EOF and completed parse before final output work | Full currentness, exact byte position/EOF/line checks |
| Before/after retained-output duplication/seal | Mandatory full checks outside optional-capture catches |
| Stream-capture construction and immediately before returning ownership of outputs | Full currentness, original output/capture checks, shared deadline still valid |
| Later writer/recovery/cursor joins and result consumption | All existing live full checks, identity bindings, and one-shot gates; unchanged |

Do not claim a universal 32 KiB speculative-work window. Native reads and the
LF cursor use 32768-byte buffers, but the ordinary parser uses 32768 UTF-16
code units, StreamReader read-ahead, and a bounded partial-line accumulator.
Preserve both parsers and all existing UTF-8, BOM, CR/LF, final-line, source,
line-length, line-count, metadata, and origin-entry rules. Do not switch the
ordinary path to the LF-only parser to obtain a simpler bound.

The proposal bounds revalidation by existing I/O/parser-refill and terminal
boundaries, not by a new timer or a hard millisecond cancellation-latency
promise. Each existing per-record checkpoint still tests CPU time and known
local cancellation. Long records crossing buffers revalidate at each refill.

### Tentative outputs and invalidation

Temporary retained/writer/semantic files and counters are not accepted inputs,
custody receipts, or route authority. If a mandatory check fails, return no
usable capture, close every tentative/duplicated output handle, and retire
the original processing attempt without restart. Existing diagnostic temp
files may remain on disk; do not delete evidence or reopen it as a substitute
for original custody. Cleanup must also cover invalidation during output
sealing and capture creation, not just while reading.

A returned capture keeps its existing live `IsCurrent` checks. A revocation
racing a successful final check must make later use fail; this proposal does
not add an atomic cross-process publication guarantee. Never treat a saved
successful boundary check as durable permission. All `LiveProofEligible`,
`NativeWriterCustodyEstablished`, and `PublicationCompletionIssued` limitations
remain as they are. No runtime, Tuna, retry, or route effect is introduced.

## Bounded implementation and verification plan, if approved

Expected production scope is the processor plus the native reader/adopter
processing guard, primarily these three files:
`GuiSmokeLogLedgerStreamingProcessor.cs`, `LocalGuiWriterFrontierSource.cs`,
and `LocalFrontierFileSeal.cs`. Preserve both PowerShell 5.1 Add-Type and
PowerShell 7 compatibility. Do not add timer workers, source copies, cached
authority callbacks, public trust switches, or a general validation framework.

1. Add deterministic regression cases before implementation. Use a small
   original-owner fixture and test-only injection at the buffer/output
   boundaries; fabricated generic streams are useful for parser equivalence
   but cannot demonstrate original custody.
2. Implement only the native-only path and mandatory checks above. Verify
   output bytes, hashes, line/origin maps, cursor indices, writer facts, and
   acceptance predicates remain identical for valid inputs. Use operation
   counts to prove full checks scale with refills/boundaries, not record count;
   do not use timing alone or assert an exact count of nested native checks.
3. Run the selected directly affected cases once after stabilization. Cover:

   - Original source versus clone/wrapper/generic source; before-begin,
     repeated begin, after-finish, and repeated processing.
   - Shared budget already mostly consumed before construction, expiration
     during buffered CPU work, and expiration after the last read but before
     seal/return. No extra read may be needed to detect local budget expiry.
   - Local cancellation/retirement while records remain buffered, including
     cancellation after EOF. Reject without retaining a usable result.
   - Persistent native ownership change after refill: reject at the next full
     boundary, including a final short buffer that needs no further read.
   - Invalidation during output duplication, capture, and before return;
     closed source/output handles, cleanup, and no partial usable capture.
   - UTF-8 split sequences, CR/LF split at buffer boundaries, maximum-length
     records spanning refills, extra/missing final lines, and cursor rules.

   Relevant existing test families are `FrontierLedgerProcessingTests`,
   `FrontierGuiSourceReadTests`, `FrontierProcessingWorkTests`,
   `FrontierRetainedOutputTests`, and `FrontierCursorMatchTests`. Select only
   affected cases, with both hosts for changed Add-Type paths; do not repeat
   aggregate analyzer/contract/Smoke lanes. If an additional broader contract
   lane is needed, run it once only.
4. Rerun the two original-process 256 MiB offline positives on final code.
   Require original data/hash checks **and** the original closed-writer/source
   join within the unchanged cumulative budget. A newly encountered failure
   becomes the next first missing edge; do not extend a deadline to pass.

This review does not promise those full-size tests will pass. GUI/parent
destination-evidence handoff remains outstanding even after that gate.
No live GUI matrix is needed to validate this offline processing contract.
The eventual live route cycle still requires a second coordinator-published
`file_tuna_v4`, not merely SHA success or fallback completion.

## Review verification and approval boundary

The 306-pin current checkpoint and its two predecessor manifests resolve to
1378 unique current pins; all matched before review. All 280 existing dirty
files remain unchanged. Only this report is added (281 dirty files total).
No tests are rerun because no executable code changes. The recovery checkpoint
is `artifacts/ledger-validation-contract-review-20260926/FINAL-VERIFICATION.json`.

Requested approval: implement this native-only bounded-validation contract,
including its explicit detection-granularity tradeoff, focused regressions,
and two offline full-size tests. Do not interpret approval as permission to
change the five-second budget, live route-cycle acceptance, runtime authority,
or GUI/parent custody requirements. Until approved, retain the current code.
