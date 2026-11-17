# Bounded Service-yield receipt consumer implementation

2026-09-24. Branch `v0.7.0`, base HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`. Implements the selected-direction,
test-only consumer from `file-transfer-peer-lineage-consumer-contract-20260924.md`.
This is not classifier enablement, a whole-peer-lineage certificate or a live run.

## Implemented boundary

The exact `filetransfer_fallback_tail_yield_evidence` event is now in the existing
GUI semantic diagnostic allowlist. Original semantic copying, source ownership,
native input capture, primary evaluation and writer verification are unchanged.
The event is not added to either Snapshot authority family. Retention is
unconditional; the new test-only consumer is default-off.

`PublicationServiceYieldProbe.ps1` runs through the existing test observer after
the original reports and before private primary cleanup. It consumes only the
original full input and completed full/compact authority views while original
custody is current. It checks exact source origins and raw-string references,
native ownership, original writer verification, selected failure identity and
local publication/forwarding. It does not reopen diagnostic files or evaluate
authority. The compact diagnostic capture shares the original full array; compact
Snapshot authority input remains separate.

The read-only matcher binds the selected source's original successful control or
bulk copy to the receiver's authenticated ingress and its own accepted receipt
association. It checks the exact returned Coordinator boundary, operation and
receipt, then follows pending-to-current or local-first history to paired release
and the selected second-Tuna handoff. Duplicate slots, substituted identities,
borrowed idempotent receipts, invalidation and ambiguous accepting transitions
fail closed. A second distinct idempotent copy is not new authority. Local
transport/installation identity is checked against the selected packet on each
writer independently, never by comparing the peers' installation counters.

The prototype remains bounded to 4,096 full records, 256 boundaries per view,
64 operations per boundary and at most 64 distinct selected original source-line
witnesses. Existing primary/native input and cumulative work checks still apply.
Only a detached diagnostic result leaves the invocation. Missing/invalid custody
or evidence gives an unproven result and cannot replace the original primary
classification, completion or acceptance.

## Fresh integration evidence

Final artifact:
`artifacts/publication-service-yield-consumer-20260924/65cee6e86ba149888f2b418806cbd9ba/`.

- Two original independent writers, 383 raw source records and 14 yield
  diagnostics. All 14 diagnostics now belong to the original primary full input.
- 339 full records, 162 compact authority records and 501 native record visits.
- 71 passing boundaries per authority view and full/compact parity `pass`.
- The original primary still makes three evaluations per host and reports
  `missing_exact_tuna_receive`, unrecovered, with the original explanation,
  witness, completion and carrier `peerLineageStatus=not_evaluated`.
- The separate consumer reports `matched_original_yield_receipts` on both
  PowerShell hosts, using 12 original source-line witnesses. It does not report
  recovery, reciprocal-yield authentication or successful Tuna payload delivery.

The genuine integration exercises the existing both-copy, receiver-peer-first
Service history. After that original primary invocation ends, a separate test
reads its already-produced artifacts to run detached query controls: 37 negative
cases, six local-security-episode checks, and four positive structural variants
on both authority projections. Control-only, bulk-only and local-first positive
variants deliberately remap detached values; they are not newly authenticated
writer histories or Snapshot-valid historical reconstructions. The fourth variant
allows an association's log record to precede asynchronous Snapshot submission.
These controls do not run another authority evaluator or supply primary custody.

Eight checks inside the original consumer invocation exercise missing custody,
replacement full/compact arrays, changed origins and fixture/witness limits.
They do not dispose or mutate the original capabilities. The standalone retention
tests also verify original semantic origins/string identity and exclusion from
authority families on both hosts.

## Development findings

The first consumer attempt incorrectly equated paired-release receipt identity
with paired-release authority identity. Original generated evidence uses distinct
projections. The matcher now requires the validated paired-release receipt and
matches release authority/generation directly between the original yield and
handoff, without equating those unrelated identity fields.

A later fresh run exposed an invalid log-submission ordering assumption. In
`423c86959bca4c329c77143bfcc85d9a`, the source local association is writer sequence
74 and its exact Coordinator Snapshot is sequence 76. Service adoption precedes
association logging (`SessionFileTransferService.cs:16443`), but the Snapshot
emitter enqueues and drains through the thread pool
(`SessionFileTransferService.CoordinatorSnapshots.cs:2987`, `:3851`). Therefore
Snapshot log submission need not precede the association's log submission.

The matcher retains exact returned-envelope/operation/receipt equality and uses
send-before-local-association and ingress-before-peer-association ordering within
each original writer. Snapshot history continuity uses the original independent
authority stream. No transport scheduling, receipt acceptance, logger scheduling
or runtime authority was changed to satisfy either development failure. Failed
attempt artifacts are preserved alongside the accepted run.

## Verification and remaining scope

Eight focused tests pass: two retention cases, the final genuine integration and
five affected compatibility/default-off regressions. Exact results, file hashes
and preservation checks are recorded
in `artifacts/publication-service-yield-consumer-20260924/FINAL-VERIFICATION.json`.
The previous review document and seal are preserved. The changes to existing
files are one diagnostic allowlist entry, the test registration/observer hook and
a corrected test comment describing retention. No application runtime, authority
catalog or classification code changes were made in this step.

Next, review the remaining whole-peer-lineage/stale-safety contract before any
classifier or acceptance change. Genuine single-copy/local-first integration
coverage and reciprocal-yield proof are not established by the detached controls.
No live-256 run is required for this diagnostic-consumer boundary. No portable
package rebuild, live GUI/NKN test, commit or push was performed.
