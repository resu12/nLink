# Cross-peer publication lineage: bounded offline prototype

2026-09-24. Base HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`, branch
`v0.7.0`. Implements the separately approved test-only feasibility slice. No
production source, acceptance predicate, runtime behavior or exported local
publication contract changes.

## Result

The current records support an exact **candidate** publication-history link,
not complete authenticated cross-peer lineage. The prototype returns
`candidate_links_only`, `PeerLineage=unproven`, with the selected missing edge
`authenticated_yield_to_coordinator_receipt`. This is an evidence boundary,
not a demonstrated transport/security defect or permission to enable the parked
`proven_stale_rejected` classifier.

The test reuses the fresh two-process Service-ingress fixture with an exact Tuna
send but no Tuna receipt. Its original primary result remains
`missing_exact_tuna_receive`, unrecovered. The wrapper still rejects current
runtime proof. No historical live capture is reevaluated or reclassified.

## What the prototype actually checks

The opt-in test hook runs after the original primary reports and before private
input cleanup. It uses the same completed, custody-owned full/compact views and
verified writer reader; it performs no new Snapshot authority evaluation. Only
the small detached diagnostic leaves the worker. A prototype exception is saved
separately and fails the test without replacing the primary result.

For both original views it resolves the old Regular send and later receiver
guard through the existing local-publication/forwarding resolver. It then queries:

- The source's exact writer, Coordinator, session, transfer, direction and old
  publication, followed by its validated local fallback-yield receipt.
- The receiver's validated paired receipt, matching the source proof identity,
  fingerprint, explicit remote yield authority and remote yield generation.
- The same receiver's carried paired-release reference and responder handoff at
  the selected second-Tuna publication, including its exact local target tuple.

The peer authority is compared through the existing evidence-token projection;
the query does not invent a hash domain. It compares the explicit *remote*
generation reference, not the peers' independently owned local counters.
The fixture genuinely has different local data-session generations. Neither
wall-clock proximity nor merged-input order supplies a causal edge in this query.

The existing fixture checks preserve complete original writer streams and exact
retained membership. Original full/compact Snapshot and route-consistency gates
must pass. Candidate references and local forwarding rows must agree exactly
between full and compact, and the primary views/line arrays must remain unchanged.

Twelve negative controls run against each detached view: foreign source writer,
source Coordinator restart, missing old publication, duplicate local receipt,
wrong peer identity/fingerprint/authority/generation, missing peer receipt,
missing handoff, wrong carried release and wrong handoff target. Each must fail
at its expected query predicate. These are selector controls, **not** claims that
tampered Snapshot envelopes passed or failed the authority evaluator.

## Remaining boundary and fixture limits

The transport records match the exact encrypted fallback-yield payload,
establishment, sequence and scope across writers. Separately, the Coordinator
records match decoded proof identity/fingerprint/authority across histories.
The selected transport projections do not carry those decoded proof references;
the selected Coordinator receipts do not carry the transport payload/installation
references. The prototype does not fill that gap by matching timestamps.

There is also a concrete fixture limitation: the sender test driver records the
local proof and its emitted effects **before** its actual transport yield send.
Those are real Coordinator receipts projected through the production emitter,
but not evidence that an actual Service send completion caused that local receipt.
The receiver uses the actual Service path. Thus this experiment must not be
advertised as a complete two-Service, authenticated send-to-receipt proof.

The missing-edge label is scoped to this selected candidate and its projections.
It is not an exhaustive finding that no other existing evidence could close it,
nor proof of security-incarnation uniqueness, durable delivery, general replay
safety or a replacement acceptance policy. The local carrier remains
`scope=local_publication_forwarding`, `peerLineageStatus=not_evaluated`.

## Verification

Evidence is under `artifacts/publication-peer-lineage-prototype-20260924/`.
The focused test runs the generated primary and prototype on Windows PowerShell
and PowerShell 7, with identical normalized outcomes. `final-focused.trx` also
covers the unchanged default-off observer/primary-preservation test.
Final result: **2/2 xUnit tests passed**, including all 12 negative controls in
each full/compact view on both hosts. Parsing and `git diff --check` also pass.
The stable generated capture is `05aa9a26f1b3400dabf550cf926a9898`; its sender-local
sequence is publication 19, old send 23, local proof 62, physical yield send 65.
The receiver-local sequence is yield receive 120, paired proof 137, second-Tuna
publication 177, rejected guard 209. These are two separate local orders, not
comparable cross-writer clocks. Both hosts retain exactly the original three
primary Snapshot evaluations and the unrecovered `missing_exact_tuna_receive`.
The default-off regression emitted no prototype diagnostic. `FINAL-VERIFICATION.json`
pins this slice's sources, final test receipt and generated evidence.

The first three development attempts failed in the new test harness: invoking
the probe as a separate script lost access to the primary's script-scoped
catalogs. Running its script block in the primary's existing script scope fixed
the harness; no production evidence check was weakened. `prototype-attempt-4.trx`
is the initial passing development run. The final receipt is the stable-source
verification, separate from those development attempts.

The existing live, no-peer and resend-binding verification seals remain
byte-identical. The historical live verdict remains FAIL with its original
62/43/19 distinct/recovered/unresolved accounting; this prototype makes no claim
about how the corrected matcher would reclassify that capture. No live test,
portable rebuild, broad suite, commit or push was performed in this slice.

## Recommended next boundary

Review the existing production send-completion and authenticated receive-to-
Coordinator paths specifically for the missing binding. First decide whether
existing issuer-owned evidence can establish it without new capture. If not,
pin a minimal evidence-only capture contract and a genuinely Service-driven
offline fixture before approving producer changes. Keep all route, retry,
rejection and delivery authority unchanged. A classifier or acceptance change
requires a separate decision; another live 256 MiB run is not the next step.
