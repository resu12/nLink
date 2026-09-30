# Production analyzer payload binding

The normal loader now connects the original parent-owned payload pair to the
retained analyzer's already-issued evaluation. This completes the payload
transport and association edge, not receipt proof or live route acceptance.
This report records the bounded implementation and offline verification on
2026-09-30.

## Missing production connection

Both normal-loader regression cases reproduced
`Production original transfer binding missing`. The transport, native handle
duplication, and evaluation binding previously existed only in the fixture.

The predecessor is `production-payload-parent-20260929/FINAL-VERIFICATION.json`,
SHA256 `2b483552b90b30243398be7d358d12f641a74ac2afc27f40586b46fbaf05e790`.
All 14,655 pinned files matched before edits. HEAD remains
`a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`; the baseline had 669
dirty entries. Before-images preserve fourteen existing text files and five
pinned build outputs.

## Payload transport and ownership

The normal loader includes `LocalCanonicalTransferPair.cs` and
`LocalFrontierPrimaryTransfer.cs`. Their logic was moved from the fixture,
which now retains only diagnostics. The production parent records its original
pair on the accepted native custody. The existing relay preparation duplicates
that pair before retiring the parent, then offers it to the original retained
worker through the existing bound channel.

The worker pulls two read-only native handles from the original parent process.
It checks the original completion hash and writer facts, including native
process creation identities, writer IDs, terminal sequences, file identities,
equal payload sizes, and matching hashes. Binary payloads remain separate from
the diagnostic catalog. No artifact path or caller-authored metadata can create
the original native ownership.

Normal parent and worker retirement now close the payload pair and channel.
The original cumulative clocks, packet bounds, producer allowance, evaluation
allowance, and five-second parent duplication allowance are unchanged.

## Binding to the original evaluation

`TryBindTransferPair` requires the original evaluation custody in its already
taken state, its exact issued evaluation pair, original context, original full
and compact arrays, and original result views. Copied or foreign inputs cannot
consume the valid original attempt. Binding is one-shot and reuses the existing
currentness checks; it does not begin, record, or run another analyzer
evaluation.

The returned `CanonicalTransferEvaluation` exposes currentness, byte count,
and the previously verified hash. It has no public constructor. Live proof,
transfer integrity, and publication completion remain false. Retirement or
budget expiry invalidates the association.

The receiver receipt to same-payload witness check remains test-only.
Coordinator authority, route invariants, runtime behavior, progress,
throughput, limits, and acceptance rules are unchanged.

## Offline verification

The stabilized build passed with 339 existing warnings and zero errors.

The first lane passed 6/6: both normal loaders, both valid complete payload
bindings, the producer versus evaluation allowance boundary, and exact
reference guards without new analyzer evaluations.

The focused lane passed 9/9: foreign completion data, retired payload pair,
retired owner, and extended, expired, and channel-retired evaluation cases on
both PowerShell hosts. These cases also verify copied-input rejection,
one-shot behavior, cumulative clocks, handle release, and false authority flags.

The affected compatibility lane passed 17/17: both canonical-source analyzer
consumers, seven receipt-to-payload comparison and exclusion checks, both
normal parent loaders, four source diagnostic contracts, and two native source
controls without a transfer pair. The retained analyzers still report three
evaluations and one native read and stop before route acceptance. The source
projection test preserves its prior hash after subtracting only the two new
normal payload attach and close hooks.

Combined result: **32 distinct passing offline tests**. Both deliberate
missing-API red results are retained and required to resolve to passing cases.
No aggregate suite or full GUI matrix was run.

The phase verifier checks predecessor hashes, before-images, exact dirty-state
preservation, build and test outcomes, script syntax, whitespace, the unchanged
live entry, and recorded child-process cleanup. Two production files and this
report are the only new non-ignored paths. No prior report or seal is edited.

## Next verification boundary

Connect the original receiver receipt to the same held payload pair and the
original witness batch. Preserve coordinator authority and keep live execution
and acceptance gated.

The live entry remains `Run-ScenarioFileTransferTunaHandoffFallback -Context $ctx`.
No portable rebuild, live NKN, live-256, full desktop GUI matrix, commit, or push
is part of this phase. A second coordinator-published `file_tuna_v4` route is
still required for the live route-cycle contract.
