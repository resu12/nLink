# C0: bounded local publication evidence carrier

Date: 2026-09-13. Base HEAD: `ef78e9d7` plus uncommitted A/B1 helpers.
Scope: offline projection/codec only. No production handoff, evaluator changes,
historical reconstruction, peer classifier or acceptance cutover.

The subsequent [C1b native producer/request review](file-transfer-local-producer-request-contract.md)
requires a separately versioned native-input format. It does not alter this V1
file-byte contract or reader. C0 remains an offline consumer without a production
caller; native framing alone does not satisfy its input/provenance premise.

## Decision and verification plan

1. Build each selected local row independently through A/B1 against completed
   full and reduced Snapshot views and their original frozen inputs.
2. Pin canonical bytes, input/location binding, omission behavior and a strict
   receiver with trusted expected request/input/payload hashes.
3. Test real generated publications on PS5.1/7, exact byte vectors, bounds,
   malformed/tampered carriers and unchanged A/B1 results. Do not wire a caller.

This is a new, deliberately narrower private format, not the parked complete
publication/peer-lineage proof bundle. Its fixed scope is
`local_publication_forwarding`; `peerLineageStatus` is `not_evaluated`. No empty
lineage list may be interpreted as a complete graph. No recovered/safe-stale,
permission, acceptance or primary-verdict field is allowed.

## Producer inputs and row selection

Inputs are full and compact argument dictionaries for the existing B1 index
constructor, plus a canonical nonempty GUID request ID and 1–64 explicit use-line
numbers in the full capture input. The dictionaries carry the already completed
Snapshot views, each exact capture/Snapshot file identity, and one shared frozen
writer input with its existing ledger validation and run/scenario ownership.

Run/scenario, catalog/contract, boundary hash and shared writer identity must
agree. Each supplied view must independently satisfy A/B1. No evaluator runs.
Locate a selected compact use by original writer/sequence and require exactly
one identical raw record. Missing, conflicting or multiply located records omit
the whole carrier; do not choose a convenient match or rewrite fields/offsets.
Duplicate requested logical uses also omit the whole carrier. This conservative
selection is not a change to any existing receive/candidate counting domain.

Each semantic row includes the local owner/scope, exact authored boundary/hash
and writer sequence, captured authored/observed generations, operation/accepted
receipt reference, complete existing route-state tuple, original use kind and
writer sequence/content hash, and the exact forwarding-effect identity/sequence.
Repeated uses of a publication remain separate rows. The 64-row limit is stricter
than the parked 64-distinct-publication ceiling; no lineage/contradiction export
is attempted. Keep the parked 256 KiB total serialized UTF-8 ceiling.

Derive rows independently from full and compact inputs, sort by ordinal writer
ID then numeric original use sequence, and require exact canonical row bytes to
agree. Physical paths/line numbers and input-container hashes stay outside this
semantic comparison. Preserve full and compact use/publication/effect locations
separately; never replace one stream's offsets with the other stream's offsets.

## Input identity and canonical format

The five named inputs are `writer`, `fullCapture`, `fullSnapshot`,
`compactCapture`, `compactSnapshot`. Each descriptor has absolute path, decoded
line count and the exact file-byte SHA-256 already checked by A/B1. The domain is
`sha256-file-bytes-v1`, including actual BOM/newline bytes. This is not the existing
GUI `utf8-lines-lf-v1` descriptor domain and must not be substituted for it.

Canonical JSON has ordinally sorted object keys, preserved array order, no
whitespace/BOM/final newline, decimal Int64 integers, and no floating-point values.
Strings escape quote/backslash; controls and all non-ASCII UTF-16 code units use
lowercase four-hex `\u` escapes. Valid surrogate pairs are retained as two escapes;
unpaired surrogates are invalid. Thus serialized output is ASCII and its character
count equals its UTF-8 byte count. Arrays remain arrays, including zero/singleton
arrays. Canonicalization must not drop location fields implicitly.

The body identifies format `nlink-local-publication-forwarding-v1`, request,
scope/peer-lineage status, run/scenario, catalog/boundary identity, all inputs,
semantic rows and aligned full/compact provenance rows. Return payload JSON,
payload SHA-256, canonical input-descriptor SHA-256 and semantic-row SHA-256 only
after every check passes. On any invalid/missing evidence, ambiguity, parity
failure, malformed value or overflow return an omitted result with no payload.
Never truncate. The builder writes no file and starts no process.

The receiver requires externally supplied expected request ID, payload SHA-256
and input-descriptor SHA-256 from the trusted producer/request context. It checks
bounds, canonical bytes, exact schema, unique rows, reference/route shapes and
location ranges. A self-declared hash or global PASS is not that external binding.
Transport/hash validation does not independently validate authority or peer
causation; the producer's trusted completed validation remains the premise.
The receiver never reads source paths, evaluates Snapshots or performs effects.

## Handoff remains a separate gate

C0 does not prove primary preservation during production execution: no caller
is added. The existing invocation-local handoff retains exact ordered arrays,
whereas finalized saved summaries omit private rows and the reduced input.
The old live artifact remains unsupported; do not reconstruct its carrier.

Before production handoff, identify the owner/lifetime of both completed views
and original inputs, pin request/receipt adoption, and test primary persistence,
exit/deadline/cleanup ordering for success and failure. Serializing C0 before a
primary response is not approved simply because errors are caught. The existing
error-only GUI companion also does not cover GUI-success/retained-failure.
Keep the original deadline, no second evaluator, no late writes after freeze,
and whole optional omission. Full peer lineage still needs generated two-writer
histories and exact local/peer/paired-release links, not fixture writer labels.

No live 256 MiB run is needed for C0. Stop before any new live run or broader
orchestration/classifier change; report which remaining binding is unsupported.

## Implemented offline API and interpretation

`tools/FileTransferOps/LocalPublicationEvidenceCarrier.ps1` defines the producer
`New-FileTransferLocalPublicationEvidenceCarrier` and the transport reader
`Read-FileTransferLocalPublicationEvidenceCarrier`. Producer `UseLines` must be
an array of Int32/Int64 values; scalar, string, fractional and null selections
are omitted rather than coerced to a different physical line. Both capture
inputs require unique physical membership for each selected logical use.

The top-level JSON keys are `format`, `requestId`, `scope`, `peerLineageStatus`,
`acceptanceRunId`, `scenarioRunId`, `snapshot`, `inputs`, `rows`, `provenance`.
`snapshot` carries `contractVersion`, `catalogVersion`, `catalogHash`,
`boundaryHash`; each input descriptor carries `absolutePath`,
`decodedLineCount`, `sha256`, `domain`. Each aligned provenance entry has
`full` and `compact` objects with `useLine`, `publicationLine`, `effectLine`.
All physical lines are original, one-based input-container positions.

Row `routeState` is the **authored publication operation's** complete tuple,
not a claim that every field was captured at the later use. In particular,
absent use data-session fields do not prove the observed incarnation. The
captured `observedStateGeneration` remains distinct from the authored one.
`effectIdentity` means the exact local `route_publication_forwarded` effect
already checked by B1. It is not a globally unique delivery or peer-proof ID.

Producer success is `ready` with `Json`, `Sha256`, `InputsSha256`,
`SemanticSha256`; failure is `omitted` with all four fields null. Reader success
is `validated_local_carrier` with a newly parsed `Carrier`; failure is `omitted`
with `Carrier` null. This reader status validates the transport contract under
the trusted producer premise, not authority independently. Unknown fields are
rejected. No source file is read by the reader.

The row and byte limits bound the output, **not whole-input scanning time**:
the producer deliberately reuses A/B1's original-input verification. No primary
worker deadline preservation, post-adoption lifecycle, production persistence,
multi-writer causation or peer-lineage result is claimed by C0. Wiring this
helper inline into the primary worker is still unsupported.

The subsequent [C1 lifecycle gate](file-transfer-local-evidence-handoff-contract.md)
now has executable retained-parent coverage: 36 controlled cases per host on
PowerShell 5.1/7. It confirms that optional pre-exit blocking/nonzero work can
change primary finalization. The C0 codec passed, but this production insertion
point did not; the next missing edge is the request-bound producer export and
its input/resource lifetime. C0 remains disconnected from production.
