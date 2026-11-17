# Whole-scenario evidence: review and bounded implementation contract

2026-09-25. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, with existing uncommitted work.
Review only. This document does not adopt alternative acceptance or authorize
a live run. It follows `file-transfer-dispatch-range-exclusion-20260925.md`.

## Decision

Do not turn the four remaining `unproven` gates into passes by extending the
meaning of the current publication receipt or controlled GUI summary.
The next bounded implementation should establish **genuine terminal and
whole-file integrity evidence in a small offline Service transfer test**.
Reuse the existing full-transfer Service test pattern. Do not graft a claimed
completion onto the seeded second-Tuna fixture.

That milestone validates one missing evidence producer and its fail-closed
checks. It is not a canonical scenario positive, does not complete the route
cycle, and cannot be joined to another attempt's rejection/payload evidence.
The current integrated alternative shadow must remain unproven. Implementation
and eventual acceptance adoption are separate decisions.

## Findings from the latest capture

Reviewed both host outputs from the latest positive two-pair capture:
`artifacts/publication-tuna-pairs-20260925/37b7d3a3ae0a42b389cecdfc11f5a499/`.
The preceding verification seal binds these outputs. No analyzer was rerun and
no historical verdict was changed.

| Required gate | Evidence currently available | Remaining obligation |
| --- | --- | --- |
| Scenario scope | Offline wrapper calls the original parent with `PayloadSize='2B'`, `Fault='none'`, and unused executable/wallet/sidecar fixtures. | Owner-bound actual canonical request, executable checks, 268,435,456 bytes, fault `switch-off`, payer `helpee`, direction and `on,off,on,off`. A scenario name is not scope proof. |
| Full route cycle | Each writer has Coordinator publications for Regular-v4, Tuna-v4, fallback-v6, second Tuna-v4. | Final fallback-v6 and all original route/epoch, runtime, payload-use and fault-action checks for the same attempt. |
| Terminal integrity | Real adapter payload pairs, but zero transfer-terminal events among 367 retained records. GUI summary is explicitly test-authored `completed=true`, `integrityOk=true`. | Both real Service completions without errors and exact expected/received whole-file size and SHA-256 under the same attempt. |
| Remaining failures | Original strict mismatch remains unresolved. The route report also says `live_route_epoch_proof_verdict=fail`. | Every original distinct candidate and every independent verdict factor, not only the selected first mismatch or a residual count. |

The last row is an important independent blocker: admitting the recorded
rejection would not remove the existing incomplete route-cycle result.
`route_consistency_verdict=pass` only says the observed routes are consistent;
it does not override `live_route_epoch_proof_verdict=fail`.

Evidence anchors:

- `PublicationPositiveIntegrationGenerated.ps1:90` owns the offline `2B` call;
  `PublicationPositiveGuiWorker.ps1:6` explicitly constructs its success summary.
- `Run-FileTransferRouteAcceptance.ps1:9500` defines the canonical scenario's
  size, fault, payer and five-route sequence.
- `SoakSummaryExtraction.ps1:1673` requires the complete Regular activation
  cycle, including final fallback and started/recovered epoch proof.
- Latest `filetransfer-route-consistency-summary.txt:7,28` records the shortened
  selected sequence and failed live-route proof. Both host reports agree.
- `SessionFileTransferSecondTunaPublicationTests.cs:42` optionally creates a
  pre-sized destination and seeds existing progress. The durable-range sibling
  explicitly checks `WholeTransferCompleted=false`; it cannot become a whole-
  file positive by hashing that pre-sized file or changing a Boolean.

## Ownership contract for a later handoff

Keep the four obligations distinct, even if a later implementation carries
their results together. A consumer must join them to one exact original attempt,
not aggregate individually valid evidence from different runs.

1. **Parent scope owner:** actual run/attempt/scenario identifiers, resolved
   request parameters, payload descriptor and executable-manifest checks.
   Explicitly distinguish offline test scope from the canonical live scope.
2. **Primary proof owner:** immutable input/source/writer descriptors, completed
   original full/compact invocation identities, validated Coordinator views,
   route-cycle evidence and complete original candidate/factor results. Reuse
   original custody; do not reopen inputs or perform another authority evaluation.
3. **Transfer/output owner:** actual inbound/outbound terminal snapshots or
   scoped terminal observations, expected source bytes/hash, destination identity,
   measured destination bytes/hash, and a completed unchanged capture interval.
   Read a closed file or hold an appropriate read lease; do not hash a file that
   can still be written or silently replaced. A caller-authored `integrityOk`
   flag is not this evidence.
4. **Join:** require the same attempt, session, transfer and peer directions;
   bind all evidence to that attempt's immutable source and producer completion.
   Missing ownership, unknown version, replay, scope disagreement or incomplete
   coverage cannot yield a pass. Diagnostics serialized afterward are outputs,
   not capabilities a caller can reconstruct and submit as owner-issued proof.

Known contradictory/failing owner evidence is a failure; unavailable evidence
is unproven. Do not convert a known failure into missing evidence merely because
the selected first failure belongs to the proposed exception. The existing
shadow stays unchanged until this handoff is separately implemented and tested.

The current `nlink-local-publication-forwarding-v2` profile is explicitly scoped
to `local_publication_forwarding` (`LocalPrimaryPublicationCompletion.cs:76`).
Its channel explicitly excludes the parent's diagnostic/terminal acceptance
semantics (`LocalPrimaryPublicationChannel.cs:28`). Keep that profile intact;
a future owner-issued scenario handoff needs separately versioned semantics.

## Complete failure accounting: required, not inferred

`AnalyzerOrchestration.ps1:6175-6363` combines independent GUI/terminal checks,
hard failures, invalid Tuna-v6, route consistency, live-route proof, preterminal
liveness timeout, warning policy, proof-specific unresolved counts, selected
proof failures and final artifact/parity overrides. Later assignments overwrite
the aggregate verdict. The final verdict and first failure are therefore not a
complete ledger of acceptance obligations.

A future default-off scenario consumer must retain each owner's original
result and applicability, including warning caps/exemptions and the parent's
warning-free policy. Preserve incomplete-proof and artifact/parity failures.
Do not enumerate only nonzero JSON fields, treat a missing counter as zero, or
subtract one admitted candidate from an aggregate count. Candidate companion
deduplication and family selection remain with their original primary owner.

Use a closed, versioned factor inventory tied to the owning verdict logic.
Unknown/missing factors, incomplete candidate coverage or exhausted bounds make
the gate unproven. The exact admitted original candidate identity may be marked
separately as observationally accepted in the shadow, but its strict `Recovered`
and every other original result remain unchanged. The actual route-proof failure
in this capture is a required regression for that future accounting work.

## Next implementation: terminal/integrity producer only

Suggested scope: a new focused test file beside
`SessionFileTransferPublicationUseTests.cs`, with a small local helper only if
needed. No production DTO, GUI harness change, wire format or receipt channel.

1. Transfer a deterministic **256 KiB** source from a fresh sender Service to a
   fresh receiver Service through the existing loopback transport. Use public
   `TryStartSendAsync` and `AcceptIncomingTransferAsync`, as the existing
   publication-use test already does at lines 38-41. Write to a new real file.
   Do not seed progress, manually set terminal state, or prefill the destination.
2. Observe both genuine completed snapshots for the exact session/transfer and
   opposite directions, with no errors. `SessionFileTransferModels.cs:192`
   exposes the required identity, state, size and error fields. Retain snapshots
   before disposing Services; verify destination persistence after closure.
3. The fixture owner captures expected source length/hash, confirms its source
   remained unchanged, and measures actual destination length/hash. Require
   exact whole-file equality, including no trailing bytes. Bind those measurements
   to the same fresh attempt and file lifecycle; a matching name is insufficient.
4. Exercise fail-closed controls for absent/failed/nonterminal peer state,
   wrong session/transfer/attempt/direction, missing destination, truncation,
   extra bytes, same-length corruption, stale result replay and changed source.
   Detached predicate mutations are controls, not genuine new terminal histories.
   Include a real interrupted transfer and a real altered-file check.
5. Expose only a test diagnostic under an explicit offline terminal-integrity
   scope. Do not inject it into the current two-writer shadow, set canonical
   scope/route gates, or emit `would_pass` for the canonical scenario. Preserve
   all existing strict results and default-off behavior.

Verify the new terminal tests and directly affected existing real-transfer test
only. This milestone needs no analyzer aggregate, portable build, live NKN or
GUI matrix. It must not add an authority evaluation or enlarge the existing
4,096-record, 256-boundary/view, 64-operation/boundary, 64-witness or cumulative
20-second diagnostic limits.

After that producer is verified, design and validate the same-attempt join with
actual scenario scope, the complete route cycle and closed failure accounting.
Only then consider a fresh targeted canonical live run, with its required build
and preflights. A separate acceptance-cutover decision remains necessary; a
small terminal test alone is not readiness for the live-256 acceptance gate.

## Review verification

Source and sealed artifact inspection only; no tests rerun because no tested
code changed. All 88 baseline dirty-file contents and all 82 pins in the prior
verification seal are preserved. This review adds only this document and an
ignored seal at `artifacts/whole-scenario-evidence-review-20260925/FINAL-VERIFICATION.json`.
`git diff --check` passes; HEAD is unchanged. No runtime, classifier, acceptance,
historical verdict, build, live test, commit or push changes were made.
