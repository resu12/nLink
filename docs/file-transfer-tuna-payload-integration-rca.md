# C2j — exact-range Tuna payload recovery integration

## Scope

This extends C2i with a separate, controlled Tuna payload fixture. C2i's
missing-resend case, C2d's missing-service-transition case and C2c's single-writer
case retain their original expectations. C2j changes tests and documentation
only; it does not change a production authority rule, recovery predicate, runtime
path, catalog or numeric limit.

The original primary now distinguishes:

| Controlled payload before delayed regular ingress | Original primary result |
| --- | --- |
| Exact chunks 12–13 sent and received on Tuna | `recovered` |
| Exact Tuna send, withheld from receiver | `missing_exact_tuna_receive` |
| Chunks 13–14 sent and received instead | `missing_tuna_resend` |

Each case still rejects the old epoch-0 regular packet at the receiver's
epoch-3 Tuna guard. Recovery is an analyzer classification of that rejection;
it does not change runtime authority or dispatch the rejected packet.

## Actual controlled execution

The two separate test hosts retain their independent logger and security
lifecycles. The original encrypted regular-v4 packet is held by the offline
packet link. The sender's Coordinator-issued fallback yield frame crosses the
real NKN data-session encoder/decoder as in C2i.

The receiver now returns its actual service-emitted local yield frame over the
same controlled regular path before committing second Tuna. The sender applies
that exact frame, obtains a paired release, authorizes and installs the retry
dispatch, and performs the actual Core prepare/drain/commit/disposition sequence.
The existing transition helper consumes this installed paired release; it must
not synthesize a replacement peer proof. The sender's epoch-3 publication and
Snapshot evidence are then forwarded to the transport.

Both test transports use the existing `FakeNknAccelerationLane`. Its negotiation
availability is controlled test evidence, not route authority. The data-session
submission uses a send authority derived from the actual current Coordinator
publication. The transport creates the encoded Tuna envelope and records its
send result. The offline link delivers those same bytes through the receiver's
acceleration-lane ingress; the receiver decrypts/decodes them and its consumer
verifies both 1,024-byte segments. Missing-receive intentionally withholds that
packet. Wrong-range changes the submitted frame to chunks 13–14, not a log row.

The sender remains a controlled Core workflow, while the receiver uses the
service transition with controlled bridge/checkpoint inputs. This is neither a
live sidecar/network test nor full sender-service/cross-peer-lineage certification.
Diagnostic peer-lineage status remains `not_evaluated`.

After that payload step, the original held regular packet is replayed twice.
The first rejection and duplicate suppression must leave transport state,
service state, service frame/hint counts and route transitions unchanged. The
dispatch history may contain the earlier yield and valid Tuna payload only;
releasing the old packet must add no dispatch or consumer delivery.

## Primary, provenance and custody

The original Wrapper, controlled GUI writer and AnalyzeRetained primary run on
PowerShell 5 and 7. Each primary retains exactly its three authority evaluations.
The scalar observer copies the already-computed candidate result, including a
recovered candidate; it does not perform another recovery evaluation.

The exact positive correctly produces no local failure witness/completion.
The negative cases retain primary-owned exact failure witnesses. The deliberately
inert executable still prevents a current-runtime/live-proof claim in all cases.

Post-run checks require complete full/compact Snapshot histories and parity,
current epoch-3 protocol-4 Tuna tuples, exact range/byte counts, matching security
and envelope provenance across distinct actual writers, and Tuna receipt before
the delayed regular rejection. Every original peer record remains in the merged
source with contiguous writer sequences and original startup/drain records.
The production retained projection is an unchanged ordered subset of that
source; it legitimately excludes unrelated log events.

## Fixture corrections during implementation

- The new outbound send callback resumed the deep receiver Snapshot operation
  inline on the transport completion stack. A test-only `Task.Yield` unwinds it;
  stack limits and production scheduling were not changed.
- Transport provenance is asserted on `filetransfer_data_path_receive_result`,
  which actually carries `actual_transport`, not the dispatch diagnostic.
- Complete two-sided authority history exceeds the small fixture's 1 MiB source
  cap. The new cases use the already-existing large-capture profile. No cap was
  raised and no authority history was removed to fit the small profile.
- A test initially equated the raw source with the retained projection. It now
  checks complete raw custody plus exact ordered membership for every retained
  record, matching the existing production filtering contract.

These are fixture corrections, not newly proven application root causes.

## Verification and next gate

Each new case has 69 full/compact Snapshot boundaries with passing authority
evidence. Final targeted regression: **6/6 tests pass**, including both PowerShell
hosts for all three new cases and all three retained negatives. Execution details
and final results are recorded in
`artifacts/publication-tuna-payload-integration-20260915/RESULTS.md`.

The bounded offline reproduction now covers the missing source, missing service
transition, missing Tuna resend, missing exact Tuna receive and exact-delivery
cases. It confirms the existing predicate, not the outcome of C2a's live failures.
No C2a artifact is rewritten or reclassified, and no claim is made that all 13
unresolved live candidates share the same cause.

The next runtime-validation gate is a new targeted live-256 activation-cycle
test, after rebuilding the portable package and running its required external
preflight. Stop before that live run under the user's existing authorization
boundary. Do not run a full matrix or change the recovery rule merely to make
the previous capture pass.

## C2k launch authorization

The user subsequently approved a reviewed local C2b-C2j checkpoint, portable
rebuild and one targeted live-256 attempt with strict preflights. Pre-launch
inspection confirmed the existing `source_dirty` provenance rejection, so the
checkpoint must precede the build and live run. This supersedes the earlier
stop-before-live authorization boundary only; no push, full matrix, recovery-rule
relaxation or historical reclassification is authorized by this step.
