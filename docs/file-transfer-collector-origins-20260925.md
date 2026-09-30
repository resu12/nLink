# Original processor line origins through primary custody

2026-09-25, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Continues `file-transfer-collector-primary-20260925.md`.
Offline and test-only; production, acceptance and runtime authority are unchanged.

## Implemented boundary

The previous milestone proved continuous native file custody but did not carry
the processor's per-line output origins into the primary. The processor already
records retained, writer and semantic output origins during its original source
read. This step transports those existing maps; it does not rescan the log,
reconstruct origins from matching strings, or run another authority evaluation.

Only the original finalizer's accepted collector/result/copy offer can serialize
the maps. The exact collector copy preserves producer bytes and line positions;
its source identity, byte count and line count accompany the three original maps.
They travel inside the existing finalizer-to-parent output packet, survive only
with the accepted completion's output owner, and continue inside the existing
parent-to-primary packet. No standalone provenance receipt or input JSON exists.

Both packets retain their 16 KiB maximum. Origin arrays have a combined cap of
4,096 entries, checked before allocation. Each map must match its output's line
count, be strictly increasing, and stay within the source. The zero-bookmark
retained map must cover every producer line. Every semantic origin must have a
corresponding writer-output coordinate. The semantic copy's original hash,
length and line count must agree with its source output. These are coordinate
and copy checks, not native writer-sequence membership verification.

The original parent binds the map's producer identity/length to its held log.
The primary repeats that comparison against its actual pending native log before
ACK. This internal comparison neither publishes the post-release descriptor nor
grants request authority. The existing release-confirmation and ownership gates
remain in their original order.

After the original reports, the existing probe first validates exact primary
custody, original input references and physical source origins. Under that
custody and the original cumulative work clock, it maps each original full-input
line to the producer, retained and writer coordinates. The mapping queries require
the original current file owner and completed source reader. Returned rows are
output-only diagnostics, not capabilities or failure-witness selections.

## Verification

**16 targeted tests passed**, with no failures or skips in the final evidence:

- Seven collector-primary tests: both PowerShell hosts, existing wrong-output-
  identity/truncated-packet controls, and three new pre-evaluation controls for
  foreign producer identity, out-of-range origin and oversized origin count.
- Nine directly affected regression tests: four collector-output tests and five
  native-primary tests. This lane ran once after the fix stabilized.

TRX evidence is under `artifacts/collector-origins-20260925/results/`:

- `collector-origins-focused.trx`: four passing rejection cases and three
  development failures; the failures remain failures.
- `collector-origins-sequencing.trx`: the three corrected cases pass.
- `collector-origins-contract.trx`: all nine regression tests pass.

The final positive attempts are:

| Host | Fixture | Full/origin rows | Compact rows | Diagnostic work |
| --- | --- | ---: | ---: | ---: |
| PowerShell 7 | `1f3580ab-5e39-4572-8f5b-85796ae49e66` | 93 | 16 | 138 ms |
| Windows PowerShell | `699622ee-372c-47be-ae81-d629f3dbadcd` | 92 | 16 | 155 ms |

Both map every actual primary source line, retain both genuine terminal rows,
preserve the original inputs/views and use the original three authority
evaluations. The original 20-second cumulative limit is unchanged. Post-run C#
assertions independently compare every reported coordinate against the producer
log and all four output files; those later reads are not analyzer inputs.

Four query controls reject copied file owners, copied source readers, zero and
past-end source coordinates. Queries also fail after original owner cleanup.
Existing exact-owner substitutions, write/rename protection and four-output
cleanup checks still pass. All five malformed primary packet cases withhold ACK,
exit unsuccessfully and perform zero authority evaluations, with files released
during rejection cleanup.

## Development failures

An initial guarded-loader compile check found an inaccessible sibling-private
source-reader field. The query now takes the candidate reader explicitly and
requires the original worker's existing exact-reader ownership check. Both host
loaders then compiled, and final positive integrations exercised both hosts.

The first seven-test run exposed one sequencing mistake in three cases: the new
pre-ACK comparison called `Describe()`, which intentionally requires producer
release confirmation. The correction compares the original pending owner's
private measurement internally, only while it is current, unclaimed and not yet
released. `Describe()` and all existing release/authority checks are unchanged.
Only the three affected cases were rerun; the four earlier passing rejection
cases fail before this comparison and remain valid evidence. No failed result
was reclassified.

## Remaining boundary

`PrimaryCollectorLineOriginsProven=true` now describes this bounded coordinate
join. It does not assert writer membership or failure-witness provenance.
`WriterMembershipVerified`, `WriterCopyProvenanceProven`,
`PrimaryProvenanceHandoffProven`, `PrimaryFileIntegrityJoinProven` and canonical
scenario proof remain false. The first missing edge remains
`file_writer_provenance_unproven`; terminal integrity is `unproven`,
`GuiIntegrityOk=false`, and the primary remains `FAIL_PROTOCOL_OR_INTEGRITY` /
`fail`.

Next: define and exercise an owned native writer-membership result in the
original source lifecycle, using its existing one-shot read and work limits.
The existing writer path is opened only for an eligible primary failure witness;
these successful Regular-v4 transfers have no such witness. Do not manufacture
one or mislabel a terminal row as a reported failure. The per-line coordinate
join can support that later design but cannot replace membership or whole-file
integrity. Acceptance and the alternative contract remain unchanged.

The new maps have not separately exercised malformed first-hop origin packets,
duplicate-but-in-range origin injection, forged valid-coordinate injection,
process death during map handoff or map-budget timeout. This trusted test workflow
is not a production security boundary. Its 262,144-byte loopback is not live-256
or canonical route-cycle evidence.

## Preservation

All 6,402 previous pins matched before work. Eight previously dirty test files
changed; all 130 other dirty files remain byte-for-byte unchanged. One guarded
test helper and this document were added. The only other previous-pin change is
the rebuilt test DLL. Production, processor/native codecs, earlier artifacts and
the preceding seal are unchanged.

Verification is recorded in
`artifacts/collector-origins-20260925/FINAL-VERIFICATION.json`.
No aggregate suite, portable rebuild, real GUI/network test, full matrix, commit
or push was performed. Optional Avalonia build telemetry was disabled for the
build invocation only; project and user settings were not changed.
