# C2c: fresh receive-guard primary integration

2026-09-15. Continues the bounded offline work after C2b. No production runtime,
analyzer predicate, authority rule or budget changes are made in C2c. C2a's
sealed live artifacts and correctness FAIL are unchanged and are not reevaluated.

## Scope and evidence

The new fixture retains a genuinely encoded regular-v4 epoch-0 payload, advances
both peers through coordinator-issued regular/Tuna/fallback/second-Tuna route
publications, and delivers that exact old envelope to the epoch-3 receive guard.
The production transport rejects it before dispatch without changing receiver
state or delivering data. Duplicate-envelope and fresh-same-range controls remain
in the reused transport fixture. No wrong-transport log observation is seeded.

The fixture now supplies a complete serialized coordinator Snapshot history to
the original retained primary, including publication-forwarding evidence before
each corresponding use. The existing whole-wrapper offline harness exercises
writer relay, source ownership, original failure selection, completion, parent
adoption and non-overwriting companion persistence on both PowerShell hosts.

This is a test driver, not a complete service/bridge transition run. Coordinator
decisions and receipts are genuine; the test projects their validated effect
receipts through the production emitter and existing service projection helpers.
These projections do not establish that the corresponding live bridge workflow
ran. Both FakeNkn peers share one process and one genuine logger/writer. Neither
writer labels nor stored Snapshot history are rewritten to simulate live peers.

## Fixture defects found and corrected

The first development iterations exposed missing fixture effect projections,
then an inconsistent fixture data-session context. The outbound authority-only
helper's first checkpoint used generation 17, but its initial state used 0.
That split the analyzer's exact lifecycle keys between preparation and commit.
The new full-history mode initializes the existing fixture identity/generation
before the first coordinator decision. Existing helper callers retain their
previous default behavior. No emitted evidence, lifecycle key, receipt validity
check, failure ordering or runtime behavior is changed to accommodate the test.

## What the original primary establishes

The selected failure is the real `filetransfer_data_path_receive_result` at
`file_tuna_v4` epoch 3, `receive_guard`, `rejected_before_dispatch`, with
`authority_effect=none`. Its local publication boundary, validated receipt and
forwarding effect bind to the exact selected source line and genuine writer.
Both original Snapshot views pass, with full/compact authority parity.

A test-only scalar observer reads the already-computed diagnostic attached to
that exact selected event; it performs no additional recovery or authority
evaluation. The rejection triplet matches, but recovery remains false with
`missing_earlier_source_send`. The fixture's exact earlier regular source record
has the same writer as the receive record, so it cannot meet the unchanged
different-writer source predicate. This is an explicit fixture limitation, not a
new analyzer defect and not an explanation of C2a's two-writer live failure.

Original primary evaluation count remains three, including the existing recovery
classification evaluation. Source hash/count traversals, receipt ownership,
primary FAIL and current-runtime-proof rejection remain unchanged. Local
publication diagnostics continue to report peer lineage as `not_evaluated`.

## Remaining boundary

C2c establishes receive-side local publication integration, not recovery or live
correctness. Before proposing a correlation fix, reproduce the exact delayed
envelope with two genuine writer lifecycles and observe its first missing edge
inside the original primary. Do not relabel same-process records, infer all 13
C2a unresolved records share one cause, loosen rejection, or add a new authority
evaluation. If this requires live rather than deterministic input, stop at the
new targeted live 256-MiB gate; do not silently launch a retry or full matrix.

Development attempts and focused verification are recorded in
`artifacts/publication-receive-integration-20260915/RESULTS.md`.
