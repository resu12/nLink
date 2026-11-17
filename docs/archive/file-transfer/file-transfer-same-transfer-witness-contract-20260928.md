# Same-transfer native receipt and witness integration contract

Date: 2026-09-28. Source/design review only. No implementation, test execution,
analyzer rerun, build, portable package, live run, acceptance change, commit or push.

## Decision

Keep canonical-fallback unchanged as the successful custody/analysis fixture
and negative route-cycle control. Do not transplant a small positive fixture
into its native processes or reinterpret its GUI-window failure as a supported
frontier failure.

A separate explicit offline profile is the appropriate integration direction.
Its complete positive is **not yet demonstrated or ready to be asserted**.
The first implementation slice should characterize one bounded delivery fault
on the original carrier pair and prove a genuine continuity-stamped frontier
repair. Stop at that first missing edge before implementing downstream witness
expectations. This review defines requirements, not approval to enable a live
path or revise acceptance.

## Baseline and evidence

Worktree: v0.7.0 at a653d4063070235c71fe211480aeb37863d9b2a4 plus existing changes.
The preceding review seal is
artifacts/reactivation-window-review-20260928/FINAL-VERIFICATION.json,
SHA-256 5d36423bc2769dc220d6f1fcfd8236005a44a1ff13463d81a2e084023d2a5bf2.
All 1,123 prior pins and 454 prior dirty entries matched at entry.

The latest canonical run remains
artifacts/origin-capacity-implementation-20260927/pwsh-canonical-04bc0771d73648bdba12c8211a483448.
It retained 73,713 semantic records, zero diagnostic receipts and no native
payload marker. It did not select a supported unresolved recent-progress
failure. Its completed fallback transfer proves neither the missing witness
nor a second Tuna publication. Those are prior verified results, not a new run
in this review. No runtime defect is inferred from this source-only work.

## Source findings

| Boundary | Evidence and implication |
| --- | --- |
| Actual original pair | FrontierAttemptNknCarrierTests.cs:88 constructs real SessionRuntime / NknSignalingTransport instances over an authenticated test pipe. The full profile keeps the held source and guarded native destination. Initial activation and fallback use transport actions and coordinator-published route tokens. |
| Existing coverage | The carrier requests initial Tuna at line 222, stops at 226, releases payload at 251 and requires fallback completion at 300. It does not schedule a frontier-gap episode or second activation. |
| Small positive setup | SessionFileTransferCheckpointRepairContinuityEvidenceTests.cs:26 constructs a private context, seeds runtime/route/data-session/sparse/checkpoint state, replaces the snapshot stream and directly invokes receive/request methods. This is useful unit coverage, not an unseeded original-pair run. |
| Small positive scheduling | SessionFileTransferFrontierBoundaryTests.cs:64 calls that seeded fixture. originalPrimaryFailure causes a real receive to emit the deferred event; it does not append a synthetic failure line. However, the fixture writes receiver-state timestamps, clears recovery state, optionally uses AfterStallForTesting, and directly forces publication after the root deadline. These mechanisms cannot be reused as genuine pair evidence. |
| App-owned admission | FrontierServiceAdmission.cs:24 requires the current inbound fallback-v6 context, data session/generation, current unsettled repair contract, an accepted frontier chunk and no pause/terminal state. It admits once and rechecks identity. Service-owned admission alone does not remove seeded setup in a unit fixture. |
| Repair authority | FallbackFrontierAuthority.cs:298 requests coordinator repair/replay authorization, issues the repair authority and validates checkpoint continuity. Settlement checks the exact current contract, request alias, transport and frontier advance before recording progress at line 1398. A non-null applicable continuity stamp is additionally needed by the producer. |
| Real receive and grace | PullTransferSessionV6.cs:5367 admits before the sparse write; 5454 records the write; the commit settles repair and arms recent grace. A later noncommitting sparse batch can log frontier_stalled. The real recent-grace callback attempts a request and records the current stall/recheck at 8297-8333. |
| Receipt closure | FrontierPublicationBoundary.cs:315 requires the exact frame/token and coordinator-accepted peer-visible completion. FrontierPublicationPayload.cs:45 prepares only a closed positive lifecycle, not an arbitrary supplied log or dictionary. |
| Ordered shutdown | FrontierAppLifetime.cs:73 seals/waits producers, prepares the catalog, submits markers, completes/flushes the writer, submits files and waits for adoption. Reuse this path; do not manually drain emitters or manufacture a marker. |
| Primary selection | AnalyzerOrchestration.ps1:9181 invokes the native diagnostic resolver only after original reports and only for the original first selected supported failure. LocalNativeFrontierWitness.ps1:54 requires that exact unresolved candidate and original full/compact views. A matching candidate elsewhere in the list is insufficient if another failure is selected first. |

Source paths in the table are relative to their existing Core/test/tool directories;
the machine-readable evidence lists exact repository paths.

## Required proof chain and stop points

1. **Original-pair identity and route.** Use the same protected 268,435,456-byte
   source, original outbound/inbound contexts, native destination and app leases
   throughout. Establish Regular v4 -> Tuna v4 -> fallback v6 through the
   existing coordinator path. Keep session/transfer identities, native file
   identities, source/destination hashes and current publication tokens joined.
   Do not construct a second service, reset the coordinator stream or borrow a
   continuity fixture.

2. **Genuine repair on that pair — first unproved edge.** A bounded offline
   delivery fault must leave a real sparse gap. Production must issue the
   receiver frontier request and current repair contract, record applicable
   checkpoint continuity, accept the actual sender's repair bytes and settle
   frontier progress. The original app service must admit its observation.
   Merely receiving data, observing a route, or obtaining a contract without
   the producer's required continuity is insufficient. If this edge is absent,
   record it and stop; do not seed a stamp, invoke private repair methods,
   initiate unreviewed bridge recovery or reissue checkpoints as a workaround.

3. **One native producer episode.** Repair must advance the contiguous frontier
   while another actual sparse gap remains. Preserve a genuine recent-grace
   arm and a real frontier_stalled event during that grace. Let the real timer
   recheck and arm the stall root. Deliver the missing original bytes after the
   recheck but before the root deadline; then let the same service capture,
   bind and complete its receiver-state publication. No timestamp edits, fake
   clocks, private context writes or AfterStallForTesting calls. A changed
   source/data session, pause, terminal or stale callback must fail closed.

4. **Receipt and writer custody.** Require the native closed lifecycle, exact
   accepted completion, one matching marker and diagnostic payload file, ordered
   completion/marker/drain and the original protected writer source. Carry them
   via existing GUI/wrapper/parent custody. Complete and hash-check the original
   transfer; shut down and drain through the production app-lifetime path.
   File completion, receipt preparation and marker custody remain distinct checks.

5. **Primary-owned selection and witnesses.** The existing primary must itself
   choose filetransfer_fallback_frontier_recent_progress_grace_recheck_missing
   as its original first unresolved proof failure. Its original event must be
   the same writer/session/transfer's frontier_stalled receiver-state record.
   Resolve exactly one eligible native receipt and 1-64 exact native source
   lines against the unchanged original evaluation-owned full/compact pair.
   Preserve semantic-to-physical origins, original completion boundary and
   marker ranges. No second evaluation or caller-selected substitute failure.

6. **Diagnostic-only result.** A matched native diagnostic must leave the
   original verdict, failed proof, unresolved/recovered counts and authority
   views unchanged. LiveProofEligible, PublicationCompletionIssued and
   NativeWriterCustodyEstablished remain false on the diagnostic result.
   It is not route-cycle acceptance or authorization for live dispatch.

## Timing constraints are requirements, not a promised schedule

FileTransferCoordinator.FrontierPublicationClock.cs:16 checks real process-owned
UTC/QPC stamps. Existing limits remain unchanged:

- Recent grace is 1,000 ms after committed repair progress.
- The original stall deadline is 750 ms after actual stall capture.
- First contiguous progress must precede that original stall deadline.
- Accepted publication must precede first progress + 1,000 ms.
- PublishedBeforeOriginalDeadline and PublishedWithinObservationBound are
  different diagnostic outcomes; neither alone proves primary eligibility.

A timely publication can leave no unresolved supported failure. A late enough
publication may instead exceed the observation bound or invalidate the episode.
Both are legitimate unavailable outcomes, not permission to delay clocks, tune
production cadence, enlarge budgets or tell the primary which failure to pick.
The candidate must survive the unchanged original classifier. The small fixture's
forced after-deadline publication demonstrates a unit-test shape, not that the
unseeded transport can reproduce it under these limits.

## Smallest implementation slice

Add a separately named, default-off test profile in the offline carrier/test
dispatch only. Do not alter existing canonical profile branches or live scenario
dispatch. Proposed initial edit surface:

- tests/nLink.SmokeTests.Core/Support/AttemptNknPipeClient.cs
- tests/nLink.SmokeTests.Core/FrontierAttemptNknCarrierTests.cs
- only the exact offline profile dispatch/test needed to invoke it

The carrier currently delivers opaque byte frames immediately. Its framing
exposes Regular/Tuna kind, lane and length, not a decoded chunk/repair contract;
Infra encrypts the transfer payload. A lane/ordinal is therefore scheduling
input, not evidence that a particular logical frontier chunk was held.

The first slice should introduce only a bounded test delivery gate: exact
original bytes, one held frame per direction at most, existing 1 MiB frame cap,
cancellation/peer-retirement cleanup, and no default-path behavior change.
Do not hold the physical writer lock or entire read loop while expecting a
later control/repair frame to overtake the held frame. Keep delivery accounting
separate from send/peer-visible completion; a held frame must not fabricate an
acknowledgment or publication result.

Whether that opaque gate can reproducibly produce the required continuity-stamped
repair is **unproved**. Characterize it once on the original pair before adding
positive receipt assertions or a more elaborate multi-stage scheduler. No random
fault sweep, repeated identical 256 MiB retry or packet-content fabrication.
If selective logical-frame scheduling is necessary, stop and design that
test-only seam explicitly; do not decrypt/rewrite payloads, splice a synthetic
data session into the service or add a production authority bypass.

An unavailable episode is a failed/unmet integration prerequisite, not a passing
positive fixture. Preserve the capture and report the first missing edge.
A later producer/witness slice is conditional on that evidence, not automatic
permission to patch Core/Infra or the resolver.

## Verification contract

Before any full-size offline integration, test only the new carrier gate:
disabled-path byte-for-byte delivery; exact one-shot hold/release; cancellation
and foreign/retired peer cleanup; capacity rejection; no false completion.
Use a focused small pair to verify that the proposed scheduling fault remains
a delivery fault rather than seeded authority. Its identities/receipt must
never be imported into the 256 MiB run.

After a producer episode is established, run directly affected producer/custody/
resolver controls once: wrong transfer/writer, missing or duplicate marker,
retired/copied payload, source mutation, absent or unsupported first failure,
witness overflow, and one-shot adoption/cleanup. Do not rerun unrelated aggregate
lanes during design or repeatedly during small edits.

Rebuild before the one intended offline integration run. Retain current carrier
180-second overall and 8-second route-edge limits, outer 600-second allowance,
existing admission/input limits, 262,144-byte diagnostic payload bound, 8-64
lifecycle entries, 1-64 witnesses and original evaluation/native-read counts.
The existing parent's 2/2/1 identity/hash/launch checks remain unchanged.
Receipt-dependent handle counts must follow existing custody logic rather than
hard-coding the zero-payload baseline. Require all owned processes and handles
retired at the end. No broad lane or full GUI matrix is implied.

Live-256 remains a later gate: diagnostic/failure-accounting and live-dispatch
integration must first be reviewed and verified. Then rebuild portable, run
escalated strict preflights, and require the second coordinator-published
file_tuna_v4 in the full route cycle. A successful offline witness cannot waive it.

## Review verification

Source inspection only; no new tests were needed because no tested code changed.
This document and ignored evidence/seal files are the only additions.
The final audit compares every prior pin and dirty entry with the preceding
seal; it does not silently accept a concurrent change. Exact audit results are
in artifacts/same-transfer-witness-contract-20260928/FINAL-VERIFICATION.json
and FINAL-CHECK.json.

