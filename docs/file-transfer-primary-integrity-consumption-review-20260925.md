# Primary integrity consumption: review and bounded next step

2026-09-25, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Follows `file-transfer-membership-origin-join-20260925.md`.
Source/artifact review only; no code, gate or acceptance change.

## Decision

Do not set `guiIntegrityOk` from the membership/origin result. The first
remaining connection is the **producer-validated transfer binding inside the
existing native file handoff**, followed by its consumption alongside the
original primary's owned membership/origin result.

Implement those together as one bounded offline/test-only milestone. Do not
introduce a generic receipt framework, another authority evaluation, a new
file read/hash pass, or a production acceptance cutover. The post-report seam
can demonstrate the join, but cannot retroactively supply an input to reports
that were already produced.

## What the current evidence actually says

Both latest positive fixtures remain pinned and were inspected:

- `artifacts/terminal-source-tokens-20260925/cbf35039-65a4-4b5e-a5c0-7243aea29438/`
  (Windows PowerShell).
- `artifacts/terminal-source-tokens-20260925/8db76bb6-9273-4f33-ad3c-e65424b75fe9/`
  (PowerShell 7).

| Connection | Current evidence | Remaining limitation |
| --- | --- | --- |
| Original writer membership to collector origins | Owned join succeeds for all 93 source rows | It proves source provenance, not payload/terminal identity |
| Source and destination measurements | Native held files, each 262,144 bytes; producer hashes equal | The handoff does not carry the producer's validated terminal/transfer binding |
| Primary completion/integrity | Completion true; integrity false; original verdict fails | No GUI summary exists in either primary directory, and no owned integrity consumer replaces it |
| Canonical route cycle | Route consistency passes for Regular-v4 only | `live_route_epoch_proof_verdict=fail`; no Tuna/fallback cycle is demonstrated |

The two terminal semantic-source lines are 87/90 and 88/90 respectively.
The owned origin maps connect them to producer lines 127/131 and 128/131.
These coordinates are corroboration from the existing artifacts, not a new
primary input and not interchangeable between attempts.

Important correction to the preceding report's wording:
`file_writer_provenance_unproven` is the test probe's hardcoded
`FirstMissingEdge`, not the analyzer's native verdict or a newly discovered
runtime failure. `PublicationTerminalSourceProbe.ps1:139-143` leaves that label,
`WriterCopyProvenanceProven=false` and
`PrimaryFileIntegrityJoinProven=false` unchanged even when the nested
`CollectorWriterMembership.OriginJoin.Joined` is true. The label is now too
coarse to describe the diagnostic progress; it must not be used to conclude
that the owned origin join failed.

The actual gate is separate:

- `AnalyzerOrchestration.ps1:2115` reads the GUI summary or returns null.
- At 3057-3059, missing integrity defaults to false.
- At 3077-3117, opposite-direction terminal completion can set only
  `guiCompleted`; the code explicitly preserves independent integrity.
- At 6179-6189, missing integrity and other independent protocol/route failures
  feed the original verdict. Later proof/artifact/parity checks remain separate.

This is an expected missing integration in the offline fixture, not evidence
of a failed payload hash or an app transfer regression. The current completion
inference is also not an owner-bound terminal-pair proof; its compatibility
behavior must not silently become the integrity predicate.

## Exact missing producer-to-primary facts

The private producer already checks substantially more than the packet carries:

- `FileTransferTerminalIntegrityEvidenceTests.cs:237` binds the real transfer
  owner and attempt, both terminal snapshots, session/transfer, directions,
  Completed states, no errors, exact sizes, snapshot hashes, source measurement,
  destination path/size/hash.
- `.SourceBinding.cs:323-372` additionally checks original log identity/hash,
  exact physical terminal origins, writer completion, unique terminal fields,
  matching session/transfer evidence tokens and completed byte counts.
- `.HeldFiles.cs:67-97` repeats the relevant checks through held handles.
  The original `SendToNativeParent` calls `CheckCurrent` before publication.

However, `.NativeParent.cs:37-66` and `Support/HeldFileNativeParent.cs:34-82,138`
transport only the owned endpoint/request/process scope, run/scenario and three
file descriptors/producer measurements. Neither hop carries the validated
session/transfer tokens or the two exact terminal origins. Matching file hashes
and labels cannot reconstruct that association in the primary.

Real terminal log rows contain the token pair, direction, size, progress,
error and reason; they do not contain the snapshot SHA-256. See
`SessionFileTransferService.cs:24048-24075`. Do not infer snapshot hash validation
from those rows or from `reason=Transfer complete.`; preserve the original
producer validation as part of the owned handoff.

## One bounded offline implementation

1. **Issue the binding only from the genuine held producer.** Extend its fixed
   test-only native packet with a compact, versioned transfer-binding section:
   session token, transfer token, and exactly two direction-specific original
   producer-line coordinates. The existing source/destination descriptors
   supply the size/hash facts; do not duplicate them in a second hash manifest.
   Construct this section only after the existing same-owner snapshot,
   file, log and terminal checks succeed. Tokens/coordinates are correlation
   metadata inside that ownership chain, never standalone credentials.

2. **Preserve the same ownership chain through both native hops.** Keep the
   binding internal to the exact `HeldFileNativeParent` and original
   request/file association. Forward it unchanged to the already-bound primary
   worker together with the three protected handles. Validate fixed schema,
   token shape, distinct positive coordinates and bounded packet length.
   Keep the existing adoption-before-release ordering, one-shot use, actual
   process binding, partial-failure cleanup and lifetime checks. Old or
   incomplete packets must fail closed for the new consumer; do not reinterpret
   version 1 as if it carried the binding. No production codec changes.

3. **Join under original primary custody at the existing post-report seam.**
   Require the exact worker, held files, source reader, evaluation custody/pair,
   context, arrays, views, running clock and successful owned origin join.
   Use the original indexed terminal records and the membership owner's
   private origin result, not `JoinedOriginsForTest` diagnostics supplied back
   by a caller. Require exactly one inbound and one outbound terminal record
   for the producer's expected tokens; resolve each to its expected producer
   line through the verified origin join. Require correct completed/no-error
   fields, exact byte counts, and equal valid source/destination measurements
   from that same native owner. Fail closed for ambiguity or disagreement.
   This is evidence consumption, not a second coordinator evaluation.

4. **Bound and retire the result.** Keep the existing 16 KiB native packet,
   4 MiB log and fixed 256 KiB payload limits. Charge any new source/event visit
   and terminal-field inspection against the existing cumulative diagnostic
   budgets; do not reset the 20-second clock or hide another traversal.
   Prefer reuse of the existing terminal selection. Preserve the 4,096 total
   record-visit, 256-boundary/view, 64-operations/boundary and 64-witness limits,
   with explicit accounting and rejection tests for the added work.
   The eligible join is one-shot; failures do not create a usable result.
   Cleanup, lost custody or expiry invalidates access. No reopening or rehashing
   in the primary, no manufactured failure witness or analysis completion.

5. **Report only the established diagnostic scope.** Add an explicit owned
   terminal/payload-join result and accurate missing-edge reporting in the
   probe. Retain original GUI integrity, verdict, gate, reports, independent
   route failures and alternative-acceptance flags. A successful offline join
   must not set `CanonicalScenarioProven`, `would_pass` or production
   `guiIntegrityOk`. Preserve old artifacts unchanged.

Suggested edits are confined to the terminal fixture partials, fixed native
helper/handoff, the guarded membership consumer and adjacent test probes.
Production runtime/coordinator, GUI summary generation and analyzer verdict
predicates are outside this milestone.

### Verification and completion criteria

Run focused new controls first, then the directly affected native-parent,
native-primary and membership/origin cases once after stabilization. Exercise
both Windows PowerShell and PowerShell 7. Do not rerun unrelated aggregates.

Required positives: both genuine offline transfers establish the owned join;
three original authority evaluations remain three; no new writer read or
primary payload hash occurs; original input views and report verdicts remain
unchanged; cleanup retires the result.

Required negatives: missing/malformed/truncated binding, wrong token or
direction, duplicate/swapped/out-of-range producer coordinates, genuine foreign
same-byte transfer binding, copied/stale file or primary owner, unsuccessful
origin join, duplicate eligible consumption, disposal/expiry and added-work
limit. Existing producer snapshot/hash/file-corruption controls remain relevant;
rerun only those touched by issuance changes. Distinguish detached predicate or
in-memory fault injection from a genuine foreign transfer/transport history.

Success is the same-transfer terminal/payload join under original primary
custody plus these fail-closed controls, **not a PASS acceptance report**.

## Before a production integrity-gate cutover

A separately reviewed change must place the owned evidence before gate/report
construction, define absent versus explicitly failed/conflicting integrity
inputs, and preserve every independent verdict factor. Do not simply OR a new
boolean into `guiIntegrityOk`, overwrite a known hash failure, rerun the original
analysis, or patch already-written reports.

The real GUI producer must supply the equivalent continuously held file and
transfer ownership; these test helpers do not establish that production path.
The 256 KiB fixture limits cannot silently become a 256 MiB contract. Actual
parent scenario scope, the complete Regular4/Tuna4/fallback6/Tuna4/fallback6
cycle and the independent-failure inventory are still required. The earlier
`2B` parent example belongs to the separate positive route integration fixture
(`PublicationPositiveIntegrationGenerated.ps1:92`), not a measured payload-size
declaration in this terminal collector's request.

Both current fixtures already have a known failed live-route proof. Even a
valid integrity join cannot erase it. No live-256 or full GUI matrix is
justified by this review alone.

## Preservation and verification

All 9,545 previous pins matched at review entry and at completion. All 158
baseline dirty files are unchanged. This document is the only added
non-ignored file. `git diff --check` and document whitespace checks pass.
The review record is
`artifacts/primary-integrity-consumption-review-20260925/FINAL-VERIFICATION.json`.

No code edits, tests, build, portable packaging, live/network run, full matrix,
commit or push were performed. Prior passing tests are inherited evidence,
not new passes claimed by this review.
