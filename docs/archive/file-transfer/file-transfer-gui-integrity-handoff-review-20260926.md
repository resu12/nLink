# Real GUI integrity handoff and writer coexistence review

2026-09-26, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Follows `file-transfer-pre-verdict-integrity-20260926.md`.
Source/contract review only. No code or acceptance change.

## Decision

Do not port the offline integrity shadow directly into live acceptance.
The next bounded implementation is **test-only reuse of the original
selected-failure writer membership, after its existing witness preparation**.
Keep the pre-verdict selected-failure reservation guard. Establish coexistence
without moving the original verdict/witness calls or taking their reader slot.

This narrower step proves resource ownership, not terminal integrity for a
selected-failure transfer. It does not enable the real GUI handoff, relax the
strict mismatch rule, adopt the alternative acceptance contract, or authorize a
live-256 run. Production promotion needs the separate producer/scope contract
below and a same-attempt integration positive.

## Verified source boundaries

| Boundary | Current implementation | Consequence |
| --- | --- | --- |
| GUI payload measurement | `GuiSmoke-Windows.ps1:2545` hashes through `OpenRead` and disposes the stream. Source measurement is at `:36859`; destination hash and a separate path-based size read are at `:37643`. | Valid scalar observations, but no continuously held payload identity through primary analysis. This is not evidence that a file actually changed. |
| GUI terminal pair | `Wait-FileTransferTerminalPairAfterBookmark` at `:2893` uses a scoped tracker; the measured call is at `:37616`. It returns parsed log fields/tracker data, not the fixture's private Service snapshots. | Do not silently substitute a GUI summary or tracker object for the C# fixture's terminal owner. Exact original source/writer origins remain necessary. |
| Parent scope | `Run-FileTransferRouteAcceptance.ps1:10793` passes the selected scenario fields. The actual child arguments at `:9348` include direction, size, route, payer, fault and toggles. | Scope must originate in that exact parent attempt/launch, not be inferred from a scenario label. |
| GUI worker completion | `Run-FileTransferTunaGuiSmoke.ps1:832` accepts writer completion after successful owned child exit/output capture. Native preparation has an earlier polling seam at `:538`; native launch at `:506` is internal opt-in. | Payload custody must be adopted before producer exit. Existing writer completion does not carry source/destination transfer-file custody. |
| Offline terminal fixture | `HeldFileNativeParent.cs:74` requires each payload to be exactly 262144 bytes; `TerminalPayloadJoin.cs:118` checks that literal size. `PublicationTerminalSourceGenerated.ps1:50` creates an ordinary Wrapper retained-source request. | The fixture proves a real 256 KiB Regular-v4 transfer, not a canonical live-256 request. Its analyzer mode is a requested proof check, not proof of scenario ownership. |

The canonical request at
`Run-FileTransferRouteAcceptance.ps1:9500` is exactly 268435456 bytes,
helpee-to-helper, helpee payer, switch-off fault,
`live-regular-activation-cycle`, `on,off,on,off`,
`RegularActivationCycle`, and the five-route sequence
Regular4 -> Tuna4 -> fallback6 -> Tuna4 -> fallback6.
None of those obligations follows from equal hashes or matching run labels.

The historical same-attempt review's `2B` request discussion concerns a
different publication fixture; it does not describe this terminal fixture.
Current analyzer terminal inference at `AnalyzerOrchestration.ps1:3115`
sets completion only, explicitly not hash/size integrity.
Historical pinned documents are preserved, not silently rewritten.

## Writer conflict: exact contract

The original publication witness validates the actual selected failure and
source/reference correspondence before opening the writer
(`LocalPrimaryPublicationWitness.ps1:31-70`). Its one membership pass chooses
the witness's writer/sequence, checks the selected source index, then stores the
verified reader in `Context.WriterReader` only on successful preparation.

The new collector path opens that same worker's `writerOpened` slot, verifies
membership using source index zero, and privately retains its own membership
owner (`TerminalCollectorMembership.cs:59-100`). Index zero is a membership
lookup, not a reported failure. Assigning this reader or its detached diagnostics
to `Context.WriterReader` would not establish the missing selected-failure proof.

There are additional constraints:

- `WriterReader.TryVerifyMembership` performs the original EOF/hash, writer
  sequence and full-source membership checks
  (`LocalRetainedSourceRequest.cs:477-571`). Its test-only private position map
  can support later exact lookups without rereading. Do not call this method a
  second time or clear the one-shot slot.
- `LocalPrimaryFrontierPublicationWitness.ps1:65` is another existing
  consumer of `TryOpenWriter`; its reader is disposed in its own `finally`.
  It cannot be assumed to leave reusable membership behind.
- The actual Frontier-native worker deliberately refuses legacy
  `TryOpenWriter` (`LocalRetainedSourceRequest.cs:358`). The terminal fixture's
  test-only native file adoption is not that Frontier-ready ownership profile.
  Reuse must not bypass this refusal or claim native production coverage.
- `New-FileTransferPrimaryPublicationCompletion` requires the exact original
  reader/custody and can dispose custody when preparation fails
  (`LocalPrimaryPublicationCompletion.ps1:25,51`). A borrower must recheck
  currentness, not resurrect a cached successful result after this failure.
- Cleanup currently closes the reader via the owning context/worker. A borrower
  may invalidate itself, but must not dispose a reader it did not acquire.

The current guard rejects any non-null selected compact failure before the
optional integrity work. Its existing synthetic tests establish safe
continuation, not successful coexistence with a real selected-failure witness.

## Next implementation: bounded offline membership borrower

Implement only under test compilation/opt-in, alongside the existing
`NativeMembership` test partial and primary observer. No production branch
behavior, wire format, live entry point or acceptance edits.

1. Retain the successful original membership object in its original reader
   using a test-only hook at the existing successful return. Retain exact
   reader, full/original arrays and original work-clock references already
   available there. Do not export or accept a JSON substitute, make a new
   membership verifier, or acquire another reader.
2. Add one bounded borrower check after the original witness/completion work
   and before cleanup. Require current exact worker, reader, custody,
   evaluations, context, arrays/views and the primary's exact selected failure
   event/source coordinate. Use the private map produced by the original pass;
   never replace the selected index with zero or accept caller-authored origins.
   Scope the result to membership reuse only; it cannot authorize terminal
   integrity, publication completion, route state or acceptance.
3. The borrower must leave original witness/completion, reports and reader
   lifetime unchanged. Expired/disposed/unavailable membership returns unproven.
   Resume and stop the original cumulative work clock around the bounded check;
   never replace or reset it. Charge only additional visits/code units.
   Keep the pre-verdict selected-failure guard; this post-witness check is
   explicitly **not** selected-failure pre-verdict integrity integration.
4. Use the small existing seeded offline publication-positive fixture
   (`PublicationPositiveIntegration_PrimarySelectsSeededOfflineFailure`) for
   this ownership test, checking its size/budget eligibility before launch.
   Label it as a seeded failure, not a genuine completed transfer. Do not merge
   its evidence with the separate real terminal fixture. If it exceeds the
   stricter prototype budget, use the existing smaller generated owned-witness
   harness, not a larger cap; report which coverage was actually achieved.

Required checks: both PowerShell hosts; unchanged original selected witness and
completion outcome; exactly one writer EOF read; no extra authority evaluation;
unchanged original reports/views; rejected copied result, wrong owner/custody,
wrong selected index/sequence, altered original reference, stopped/foreign clock,
expired or disposed owner, and duplicate consumption. Test that disposing a
borrower does not close the owner and that owner cleanup permanently invalidates
the borrower. Preserve omission paths for unavailable/failed witnesses and the
existing Frontier consumer; no reopening to rescue an omission.

Run new focused tests first, then only affected membership/primary and selected
publication-witness/completion tests. At most one relevant broader contract lane
after stabilization. Do not rerun unchanged aggregates or GUI scenarios.

### Accounting clarification

The current offline prototype caps cumulative visits at 4096 and uses the
original cumulative 20-second clock. Preserve those stricter limits for this
prototype, including new lookups/projections; do not reset counters between
consumers or count already-performed membership work twice.

These are **not** the shared production constants:
`LocalNativeInputCapture.cs:10-23` currently sets InputRecords=262144,
ProducerRecords=2097152 and ProducerMilliseconds=20000. The shared producer
code-unit limit is 1073741824; prototype-specific limits remain independent.
This review changes none of them. Large existing route captures are not
automatically eligible for the small terminal prototype.

## Later real-GUI handoff contract

The following requirements remain necessary, but are not part of the immediate
borrower prototype:

- Freeze the actual parent attempt/selected scenario and launched arguments,
  including measured phase and executable/runtime identity. Bind those owned
  inputs to GUI request, collector completion, retained-source request and
  original primary invocation. Serialized labels/manifest fields are
  correlation data, not the capability.
- Acquire/measure source through one protected handle after payload creation,
  retain it through the measured transfer, and acquire the finalized destination
  through a protected handle after the exact terminal pair. Obtain length,
  identity and digest through those same handles. Preserve existing timeout/
  cancellation behavior; use bounded acquisition, not an unbounded wait.
  Destination identity continuity begins at acquisition unless an earlier
  app-owned file handoff is separately implemented and tested.
- Bind each terminal to its actual peer writer, session/transfer, role/direction
  and original source line. The two-app GUI must preserve both writers' native
  provenance through collector outputs; the single-writer fixture does not
  supply that proof. Matching filenames/timestamps/hash-search results may
  nominate a candidate for acquisition, but cannot establish transfer ownership.
- Duplicate/adopt protected payload custody into the designated parent before
  GUI producer exit, acknowledge that adoption, and keep continuous protection
  through the original primary. Preserve the successful GUI exit requirement.
  Each relay hop needs its own exact endpoint/child identity, one-use adoption
  and lifetime rules; normal exit after acknowledged adoption is distinct from
  death/cancellation before adoption. No reopening by path after a custody gap.
- Keep payload descriptors separate from the existing Frontier diagnostic
  payload catalog (`LocalFrontierFileManifest.cs:14-16`); its 256 KiB diagnostic
  payload limit is not the transfer size contract. Do not enlarge it or repurpose
  `nlink-local-publication-forwarding-v2` as a transfer-integrity capability.
- Only after these edges and selected-failure coexistence are proven should
  same-attempt integrity be consumed before verdict. Keep the complete original
  candidate/factor inventory and independent route-cycle result. A known failed
  route gate remains failed even when payload integrity is proven.

Production acceptance at
`Run-FileTransferRouteAcceptance.ps1:4776-4827` still checks the measured phase,
both terminal states/errors, integrity and route/protocol. Timeout diagnostics
are not a substitute acceptance path. No old report is promoted by this review.

## Verification and preservation

All 12728 pins from
`artifacts/pre-verdict-integrity-20260926/FINAL-VERIFICATION.json` match.
All 170 pre-existing dirty files are preserved byte-for-byte; this document is
the only new non-ignored file. HEAD and branch are unchanged.
`git diff --check` and this document's whitespace check pass.

The review seal at
`artifacts/gui-integrity-handoff-review-20260926/FINAL-VERIFICATION.json`
references the previous seal by SHA-256 and pins the reviewed sources/document.
This is source review, not a new runtime RCA or test pass. No tests, build,
portable package, live run, full matrix, commit or push were performed.
