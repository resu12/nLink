# Recorded security-context spans: review and bounded consumer contract

2026-09-25. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, including the existing uncommitted
diagnostic retention and original-capture consumers. This step is review only.

## Decision and exact claim

The retained evidence supports a default-off, test-only query reporting
`matched_recorded_security_context_spans`: the selected envelope diagnostic rows
name the same local context as unique recorded install/retire markers and occur
strictly between those markers in the verified writer order, without an observed
conflicting lifecycle record for that transport in the span.

This is **recorded lifecycle consistency**, not proof that security remained
valid at every execution instant. Keep `SecurityLifetimeProven=false`,
`RuntimeRetirementProven=false`, `ClassifierEnabled=false`,
`AcceptanceChanged=false` and `Live=false`. Do not rename the result to
authenticated lineage, stale safety or `proven_stale_rejected`. The strict
`missing_exact_tuna_receive` result remains independent and unrecovered.

No additional runtime event, lock, scheduling change, authority catalog,
retention extension or evaluator is indicated for this bounded query. The next
implementation can reuse the existing original-capture integration. It does not
need a live-256 test or establish live-test readiness.

## Producer semantics and timing limit

The relevant current code was inspected directly:

- `NknSignalingTransport.SecurityEvidence.cs:76` captures the original context
  reference with a key copy under `controlSecureStateGate`. Context identity,
  installation generation, establishment fingerprint and session are immutable;
  `Retired` is a volatile flag. No runtime decision consumes this diagnostic state.
- `NknSignalingTransport.FileTransferTransportChannel.cs:6317` retires the prior
  context and installs a new one under that gate, including a same-key reinstall.
  Reset retires the old context before clearing keys. Codec sequence numbers can
  restart; equal sequence/session metadata does not identify an installation.
- Encryption and decryption use the captured key/context; diagnostic evidence
  retains that reference. `FormatFileTransferSecurityEvidenceSuffix` samples the
  original context's current retirement flag later, not at key acquisition.
- `LocalOperationalLog.cs:55` receives an already formatted message. Writer
  sequence assignment happens under the separate log gate. Thus formatting
  `captured`, logging a retirement on another thread, then submitting the older
  formatted message is possible from the code's ordering. This is a permissible
  interleaving, not a reproduced defect or evidence that it occurred in the fixture.
- Yield copy status and its security suffix are sampled separately; receipt
  association status is sampled again. Association rows carry an evidence ID and
  receipt binding, not their own complete security tuple. The rejection companions
  share the original envelope context, and some suffix text is reused. Eight
  diagnostic rows must not be presented as eight independent cryptographic uses.

Consequently, an out-of-span or retired diagnostic makes this query unproven; it
does not by itself establish a runtime violation. Conversely, a bracketed captured
row cannot prove a callback ran before retirement, that earlier preparation had
no effect, or that a Coordinator publication was retired. Do not add logging locks
to manufacture a stronger conclusion. The existing `YSecurity` failure label
`security_lifetime` checks recorded shape/status, not this complete history.

## Saved evidence reviewed, not reevaluated

Accepted capture:
`artifacts/publication-dispatch-window-20260925/f4e6c7d8de3f4b82a2a9f8c1f68e8e57/`.
Its two host outputs have identical retained bytes and agree on the selected
origins. Saved results contain 343 full and 162 compact authority records,
71 passing boundaries per view, three original evaluations and 21 witnesses.
No new consumer, authority evaluation or test ran during this review.

| Writer-local context | Install sequence / source line | Selected diagnostic sequences / source lines | Retire sequence / source line | Verified capture end |
| --- | --- | --- | --- | --- |
| Sender `p44204r3191516ef6f34cd2`, generation 1 | 11 / 17 | Old send 23 / 33; forward send 68 / 173; reverse receive 84 / 211 | 151 / 340 | 153 |
| Receiver `p38864rab303dbd8774459b`, generation 1 | 13 / 15 | Forward receive 123 / 174; reverse send 151 / 223; rejected receive 220 / 328; mismatch 221 / 329; ignore 222 / 330 | 228 / 343 | 230 |

Each writer's selected rows match its own transport ID, installation ID,
generation, establishment fingerprint and security session. The two local
transport/installation identities differ. Their establishment/session values
match the already selected envelope relationships; peer-local generations are
not a joining key. The reverse receive's merged physical line precedes the
reverse send-result line: merged order is not cross-peer causality.

There are four lifecycle rows. Adding their origins to the prior witness set
would produce 24 distinct lines, because receiver retirement is already the
dispatch query's last retained row. This is feasibility arithmetic only, not a
new issued witness set. A detached review extract is saved at
`artifacts/security-lifecycle-contract-review-20260925/saved-artifact-review.json`.

## Bounded implementation contract

1. Add a separate opt-in query after the existing original yield, reciprocal,
   rejection-chain and dispatch-capture checks. Require those prerequisites to
   have completed successfully, preserving their results on any new omission.
   A dispatch observation count is not security authority; do not require zero
   merely to make the new lifecycle diagnostic positive.
2. Select exactly the eight envelope diagnostic rows already chosen by those
   chains: old send; forward send/ingress; reverse send/ingress; rejected
   receive/mismatch/ignore. Derive their positions independently from each
   original authority view, not from saved artifacts or a new broad candidate
   search. The initial scope is two writers with one selected local context each.
   Receipt-association and unselected copy rows are not independently certified
   by this new result; their existing checks remain unchanged.
3. Reuse the live private context, completed views/evaluations, original index,
   arrays, origins and native writer custody. Apply `DCheckCapture`/complete
   semantic-source coverage and obtain each selected writer's endpoint through
   the existing verified writer metadata, including the sender. No raw-file read,
   saved-summary read, reader reopening, copied capability, second evaluation or
   source append is permitted inside the consumer.
4. Require canonical, unambiguous selected fields: writer and positive Int64
   writer sequence; `ftst1-`/`ftsi1-` plus 32 lowercase hex digits; positive Int64
   installation generation; `ftest1-` plus 64 lowercase hex digits; and the
   projected `fte1-` session with 24 lowercase hex digits. Require recorded
   `security_context_status=captured`; keep the existing envelope, payload,
   secure-sequence, scope, yield-bound and publication checks. Compare local
   tuples exactly, never infer them from equal payload metadata or current state.
5. Inspect all retained lifecycle rows in the bounded original capture, not just
   the two nearest matches. Recognize the catalog's case-insensitive event
   membership, then require canonical event spelling and fields; wrong-case or
   malformed lifecycle evidence must not disappear through an exact-name filter.
   Duplicate structured fields, ambiguous writer/transport ownership, or an
   incomplete relevant identity are explicit unproven. Exclude an unrelated
   writer/transport only when its recorded identity is usable and unambiguous,
   after checking for reuse/conflict of either selected installation ID. An
   unrelated record cannot supply missing fields to a selected one.
6. For each selected local installation require exactly one install and one
   retire record across that captured writer history, with the complete selected
   tuple, install status `captured`, retire status `retired`, and
   `authority_effect=none`. Reject reuse of a selected installation ID with
   different ownership/metadata, or assignment of its transport/generation pair
   to another installation, including conflicts beyond the immediate selected-use
   interval. Require
   `install < every selected diagnostic < retire <= verified capture end` in
   that writer's sequence. Do not substitute writer EOF, Snapshot completion,
   another generation or a peer's retirement for a missing marker.
7. Between the selected install and retire markers, no other install/retire for
   that transport may be observed. A reinstall with the same key, establishment
   or session still cannot substitute a new installation ID/generation. Well-formed
   other installations outside this closed span need not invalidate it; reject
   overlaps, ambiguous reuse and conflicts with the selected identity. Do not
   enforce a shared or monotonically increasing secure-envelope sequence across
   send/receive directions or peers. Log submission order is the only ordering
   claim here, not timing of key use or receipt application.
8. Normalize the two independently selected results and require full/compact
   equality. Lifecycle diagnostics come from the shared original full capture,
   not the compact authority array. Add the four lifecycle origins to the
   existing deduplicated witness set, not a second allowance. Return only detached
   scalar/identity/origin diagnostics; recheck original custody, endpoint identity,
   unchanged arrays/views and elapsed work before publishing the optional result.

Keep 4,096 full records, 256 boundaries per view, 64 operations per boundary,
64 total witness lines and the existing 20-second cumulative work limit, plus
all inherited native byte/record limits. Overflow, missing evidence, conflicting
evidence or unavailable custody yields a precise unproven/omitted reason, never
truncation, a relaxed budget or a search for a more convenient installation.

## Verification plan

Use the existing fresh two-process Service-yield history for the genuine positive
integration on PowerShell 5.1 and 7. No new runtime fixture is needed for the
recorded-span claim. Do not require the saved literal IDs, positions or counts
above; derive the original rows and report actual counts.

Detached query controls must cover both peers independently: missing/duplicate
install or retire; tuple, status, case, generation and numeric-overflow errors;
wrong or unknown writer/transport; same-key-like replacement identity; conflicting
selected-ID reuse; overlapping/intervening installations; a selected record
outside the closed span, including captured text logged after retirement; and
foreign/early/missing endpoints. Also cover unrelated valid contexts and distinct
non-overlapping installations, so the query does not reject all multi-context
captures. These are constructed diagnostic controls, not authenticated histories.

Verify full/compact disagreement, shared witness overflow, unchanged original
arrays, replacement origins/custody and optional prerequisite omission. Controls
must reuse unchanged raw Snapshot strings and stay within the same work budget.
Check that failure preserves the earlier yield/rejection/dispatch results and
the original strict primary result, explanation, completion and three evaluator
calls. Preserve `peerLineageStatus=not_evaluated` on the original carrier.

Run only the new focused integration/controls and directly affected prerequisite
omission/default-off regressions after implementation stabilizes. Do not rerun
aggregate lanes, unrelated copy permutations, the GUI matrix or a portable build
for this diagnostic-only change. If a stronger execution-lifetime or acceptance
claim is needed, stop and review that separate contract rather than widening this
query or changing production timing.

## Review verification

All 42 pins in the preceding retention seal and all 64 pre-existing dirty-file
contents remain unchanged. This step adds only this document and ignored review
artifacts under `artifacts/security-lifecycle-contract-review-20260925/`.
The final review seal is `FINAL-VERIFICATION.json` in that directory. No tests,
evaluators, runtime/consumer edits, acceptance change, rebuild, live run, commit
or push occurred. Prior passing test receipts remain prior evidence, not newly
executed validation of the proposed consumer.
