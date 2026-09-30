# Same-transfer Service producer and authenticated-capture boundary

2026-09-26, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Follows `file-transfer-selected-collector-origin-20260926.md`.

## Outcome

A new offline fixture now keeps the actual Service-issued publication use, one
explicitly seeded transport-mismatch observation, both completed Service
terminals and both 256 KiB files under one private transfer owner. A second real
transfer with identical bytes cannot supply that owner's evidence.

This is **not a completed selected-witness/terminal integration positive**.
The original primary selects this fixture's mismatch but correctly omits its
forwarding witness because the loopback transport has no authenticated NKN
security/envelope capture. The terminal/payload consumer remains disabled.
The first remaining edge is now specific: capture authentication and envelope
provenance from the same actual Service transfer and its NKN data-session send.

## Producer implementation

The existing real-file transfer helper has a default-off private fixture option.
It still sends real source bytes through two `SessionFileTransferService`
instances, saves the actual receive destination and waits for Service-owned
source streams to close. Existing Regular-v4 and cancellation cases keep their
original behavior.

The new owner enables the existing test loopback Tuna-v4 readiness evidence.
The Services' own coordinators admit and publish their routes. At the first
chunk-batch send, the fixture obtains the exact authority already recorded by
the loopback and calls `FileTransferPublicationUseEvidence.GetSend` on it.
It does not construct another coordinator, authority, publication receipt or
publication-use DTO. The source Service's production code has already attached
that publication use at its physical-submission boundary.

The synchronous send callback logs one observation labeled
`offline_fixture_fault=wrong_physical_transport` and
`offline_fixture_scope=same_service_transfer`. Its route/generation/range and
publication fields come from that submission. Only the reported physical
transport is deliberately wrong; original loopback delivery continues
unchanged. This is neither a live defect nor proof of a real Tuna socket.

The owner captures each terminal snapshot directly from its original Service
before disposal, retains those exact objects and the private transfer run, and
uses the existing completed-file/source-log checks after cleanup and logger
drain. No records are appended after drain. Detached diagnostic JSON is output
only, not input to the primary or any terminal capability API.

Copied owner, authority, publication-use, failure-row and terminal objects are
rejected. The foreign-transfer case additionally rejects a foreign owner,
relabeled owner, payload capture, publication use, selected failure and terminal
rows even though the two real destination SHA-256 values are equal.

## Why the joined positive remains unavailable

`Resolve-FileTransferPublicationUseEvidence` in
`tools/FileTransferOps/LocalPublicationRows.ps1` requires
`security_context_status=captured`, genuine security installation/payload and
envelope fingerprints, and matching projected transfer/session identities.
The loopback producer supplies none of these fields. Its actual Service
publication and snapshot stream do not substitute for them.

Both PowerShell hosts preserve the original `carrier_forwarding_unproven`
witness omission. Preparation succeeds, but no publication completion or
borrower is issued. The primary still reports `FAIL_PROTOCOL_OR_INTEGRITY`,
gate `fail`, first proof failure `file_tuna_data_path_mismatch`, and exactly
three original authority evaluations. All adopted collector outputs are
released by original cleanup. The new test-script switch checks this precise
negative boundary; its default path still requires the existing positive
witness/completion contract.

Earlier development attempts exposed an incorrect fixture snapshot assumption:
Service snapshot getters recreate records (including internal observation
state), and disposal clears current snapshots. The final fixture captures the
original objects once before disposal rather than comparing separate reads.
Failed attempts remain in the artifact directory; they are not passing evidence.

## Verification

The final two new cases pass on Windows PowerShell 5.1 and PowerShell 7:

- Single transfer: `2a97d25f09324df9b4ef3ddd521e5904`.
- Equal-byte foreign transfer: `18a7badf566e47509bea575ffcf1bf20`.

Both select retained source line 37, original writer/producer line 53. The single
transfer has 138 original writer records, 89 full and 16 compact input records;
the foreign case has 268, 176 and 32 respectively. Their real payload size is
262144 bytes per transfer. No extra analyzer evaluation is introduced.
Final results: `artifacts/same-transfer-publication-20260926/results/producer-final.trx`.
One directly affected compatibility lane also passes all 18 cases: existing
terminal-integrity/source binding, native witness positives and omissions,
profile refusals, and the original NKN publication consumer. Together with the
new cases, all 20 final targeted tests pass, with no skips. No aggregate lane
was run. Both script parsers, the test build and whitespace/diff checks pass.

The previous milestone's 1132 pins matched before edits. Of 16817 effective
inherited pins, only the three intentionally edited existing test/support files
and rebuilt test DLL differ. The other 191 pre-existing dirty files are
unchanged. No production source, acceptance rule, mixed-profile refusal or
pre-verdict reservation changes. No portable build, live-256 run, full GUI
matrix, commit or push is part of this step.
There are 198 dirty entries after adding three test/support files and this
report. The incremental evidence seal is
`artifacts/same-transfer-publication-20260926/FINAL-VERIFICATION.json`.

## Next bounded step

Build the authenticated producer on the existing FakeNkn approval/encryption
fixture, with the real file-transfer Services using those exact NKN data
sessions. Capture the actual send's security/envelope evidence while its
Service-issued publication is current and before logger drain. Keep the failure
explicitly seeded and require real completed files plus equal-byte foreign-owner
rejection. Do not paste an independent security session's evidence onto this
loopback observation, fabricate fingerprints, or relax the original witness.

Only after that original witness succeeds can the post-report terminal/payload
handoff be integrated. Continuous native payload custody, two independent GUI
writers and the canonical live-256 five-route cycle remain separate later gates.
