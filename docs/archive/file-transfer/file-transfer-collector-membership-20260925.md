# Owned native writer membership in the original primary

2026-09-25, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Continues `file-transfer-collector-origins-20260925.md`.
Offline and test-only; production, acceptance and runtime authority are unchanged.

## Implemented boundary

The original primary can now issue a bounded, invocation-owned membership result
for its retained writer stream, without needing or manufacturing an eligible
reported-failure witness. The lookup is the first actual original source record;
it is not a selection for publication acceptance or terminal-integrity proof.

The guarded helper duplicates the original retained writer's native handle with
read access and supplies an **unverified** lease to the unchanged production
`WriterReader.TryVerifyMembership`. There is no pathname reopen, preliminary
hash scan, replacement verifier, or new authority evaluation. The original
verifier traverses the whole stream, verifies its hash at EOF, checks writer
sequence/start/scope/counts, and matches every original full-input record.

The original worker stores that reader in its existing private writer slot and
uses its existing one-shot open guard. Its separately owned membership result
requires the original worker, four-output owner, source reader, primary custody,
completed evaluations, context, arrays and views. The original input snapshot,
native code-unit accounting and running work clock must also match before read.
It retains the 4,096 combined record-visit and 20-second cumulative work limits;
the unchanged verifier enforces its original byte/code-unit bounds.

The result is private to the original worker's lifecycle. JSON contains only
diagnostic scalars, never an accepted token. Copies and substituted primary
arguments cannot consume it; copied-result disposal does not retire the original.
Original cleanup retires the membership and closes the writer. A failed or
partial attempt closes its reader and consumes the same one-shot opportunity.

The existing publication-witness `Context.WriterReader` slot stays empty, so the
legacy `WriterMembershipVerified` field remains false. The new, separate
`CollectorWriterMembership.Verified` field describes this bounded verification.
Neither field is relabeled as full provenance or an acceptance result.

## Focused verification

All four final focused tests passed in
`artifacts/collector-membership-20260925/results/collector-membership-owned-inputs.trx`:

- PowerShell 7 and Windows PowerShell verify the complete original writer.
- A foreign lookup traverses the stream but cannot issue membership.
- A one-record partial read cannot issue membership or a verified writer.

The two final positive fixtures are `2bf9a253-1a93-4727-bf10-a3d65bc0c226`
(PowerShell 7) and `5a46d4df-1e10-4054-b04e-ccbfa74d6729` (Windows PowerShell).
Both verify 135 writer records against all 93 original full-input records, with
363 verifier record visits and 677,251 cumulative code-unit visits. Diagnostic
work measured 156 ms and 178 ms respectively. Both preserve all original inputs,
two genuine terminal rows and the original three authority evaluations.

Each case rejects 12 substitutions before any writer read: copied owner, files,
reader, context, custody, evaluation pair, full/compact arrays, missing view,
copied original snapshot, wrong code-unit accounting and a foreign clock.
Each positive rejects nine post-read ownership substitutions. Repeated membership
verification and the legacy writer reopen are rejected in all four cases.
Cleanup checks prove membership retirement and writer closure.

## Regression result and unresolved capture failure

The seven-test directly affected collector-primary lane ran once:
**six passed, one failed before primary launch**. A single diagnostic rerun of
that failing case passed. This is not a clean seven-test lane or proof of a fix.

The generic `filetransfer_gui_evidence_capture_not_persisted` error occurred twice:

- Initial focused PowerShell 7 attempt:
  `f65edaaf-4f4e-48bc-ad0d-7405cac49831`.
- Collector-primary wrong-output-identity regression attempt:
  `5233f11f-9662-4c1c-8308-9fbecb873f0b`.

Both actual GUI fixture children exited with code 1; neither attempt created a
primary analysis directory or persisted the semantic copy. The recorded parent
deadline did not time out. The original finalizer caught the inner exception and
later threw its generic persistence error without retaining the underlying
cause. The failed artifacts cannot establish whether the internal capture-time
budget, another ownership check, or another operation caused the error. No root
cause or runtime fix is claimed, and no deadline has been relaxed.

The test-only retained-output finalizer seam now preserves the original exception
and script stack in `collector-capture-failure.txt`, capped at 8,192 characters.
It leaves the original integrity reasons, generic throw and cleanup unchanged;
diagnostic-write failure cannot mask the original failure. The isolated rerun
`de7a173e-ee4f-457d-8b2a-72aa30e09d92` did not reproduce the problem.

`artifacts/collector-membership-20260925/verify-finalizer-diagnostic.ps1` directly
exercises the transformed original catch body on both PowerShell hosts. Its
three controls per host verify original exception retention, truncation at the
bound, and diagnostic-write failure preserving original integrity reasons.
These are diagnostic-path checks, not reproduction or acceptance evidence.

All 16 test attempts remain recorded: 14 passed and two failed. Three initial
passing development attempts predate the added original-accounting guard and
are superseded by the final four-case focused run. The two failures remain
unresolved failures, not reclassified passes. All four TRX files are retained.

## Remaining boundary and next step

The original primary still reports `file_writer_provenance_unproven`, terminal
integrity `unproven`, `GuiIntegrityOk=false` and `FAIL_PROTOCOL_OR_INTEGRITY` /
`fail`. Writer-copy provenance, full primary handoff, whole-file integrity and
canonical scenario proof remain false. No failure witness or completion token
was issued. The alternative acceptance contract is unchanged.

Before declaring the regression gate clean, obtain the original inner exception
from a bounded targeted reproduction of the pre-primary capture failure; do not
rerun aggregates until green or tune an unproven timing cause. After that edge is
understood, the next provenance step is to consume the same owned membership
with the original per-line origins, then join terminal/source/destination
integrity evidence under original primary custody, without another evaluation.

Malformed writer hash/sequence/content, forced clock/code-unit exhaustion,
process death during verification and coexistence with a genuine eligible
publication-failure witness have not separately been injected on this new path.
This trusted test workflow is not a production security boundary. Its
262,144-byte loopback is not a live-256 or canonical route-cycle test.

## Preservation

All 7,112 previous pins matched before work. Seven previously dirty test files
changed; all 133 other dirty files remain byte-for-byte unchanged. Three test
files and this document were added. The only other previous-pin change is the
rebuilt test DLL. Production, processor/native codecs, earlier artifacts and
the preceding seal are unchanged. PowerShell parse checks and `git diff --check`
pass. Optional Avalonia telemetry was disabled for the build invocation only.

The verification record is
`artifacts/collector-membership-20260925/FINAL-VERIFICATION.json`.
No aggregate suite, portable rebuild, real GUI/network test, full matrix, commit
or push was performed. This checkpoint records a verified membership boundary
with an unresolved fixture failure; it does not claim live-test readiness.
