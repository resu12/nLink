# Collector output custody through successful GUI exit

2026-09-25, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Continues `file-transfer-collector-finalizer-20260925.md`.
Offline and test-only. Production, acceptance and runtime authority are unchanged.

## Implemented boundary

The preceding fixture joined the protected collector output to the original
finalizer, but did not retain the processor's output files through child exit.
The new opt-in fixture calls the existing `ProcessFromStreamWithRetainedOutputs`
instead. Its original read-only native duplicates retain the original file
objects and their deny-write/delete sharing. The production processor is unedited.

Because those handles prohibit renaming, this test-only finalizer seam creates
the three outputs at their exact final names with the existing `CreateNew`
semantics, rather than creating temporary names and moving them. It replaces the
ordinary semantic copy with the existing owned `CopySemantic` pass. That pass
copies and hashes once, verifies the original processor facts, and retains the
fourth file from creation. It is not an additional scan or authority evaluation.
The original capture's 250-ms clock remains; the new copy has a five-second cap.

Only the original finalizer, collector, processor result and owned semantic copy
can offer these four output handles. A separate test-only channel is bounded to
16 KiB and scoped to the original request, bound child PID/creation time, run,
scenario, root and source bounds. The original parent pulls read-only duplicates
using its already-held child process handle, not a reopened PID or file path.
It checks native identities, lengths and fixed output roles before acknowledging.
The child waits at most five seconds, also observing parent death and cancellation.

This acknowledgement is provisional, not completion. The existing finalizer
validation, original `Complete`, child exit, stdout/stderr capture and parent
persistence gates remain. Only the completion actually returned by the original
`TakeAfterExit` can acquire the pending output owner. Copied request, completion
or output-owner objects cannot adopt or retire the original. The parent closes
pending handles and the extra channel in its existing cleanup path.

## Verification

The four new focused tests pass on the final implementation:

- Windows PowerShell and PowerShell 7 preserve all four outputs through original
  finalizer cleanup and successful child exit.
- A genuine nonzero child exit rejects completion and output adoption.
- A failed original stdout/stderr capture rejects adoption even with exit code 0.

The directly affected nine-test regression lane also passed: the two existing
collector/finalizer integrations, five native-collector integrations and both
original GUI parent/finalizer fixtures (25 cases per host). It ran once after
the implementation stabilized. In total, **13 targeted tests passed**, with no
failures or skips in the final lanes. Their TRX files are
`artifacts/collector-outputs-20260925/results/collector-outputs-focused-final.trx`
and `artifacts/collector-outputs-20260925/results/collector-outputs-contract.trx`.

The new child tests reject copied collector, issuer, processor-result and semantic
copy offers, and reject a repeated valid offer. Existing pre-read/capture controls
remain. On each successful run, eight write/rename attempts fail with actual
Windows sharing violation 32 after child-local cleanup, and eight more fail in
the parent after child exit. Disposing a copied owner leaves the original valid.
After disposal, all four files admit write-open; both channel events are gone.
Both failed-completion controls also release all four provisional file handles,
close the channel, and perform zero primary authority evaluations.

Positive attempts:

- Windows PowerShell: `47b527d6-0fea-412b-9255-abe6d7c10aab`.
- PowerShell 7: `0b6ed512-56f6-4250-9292-c31827f124b6`.

After the explicitly bounded parent-custody fixture ends, the existing primary
still runs its original three authority evaluations, retaining both genuine
terminal rows and unchanged input/result views. Diagnostic work took 123 ms and
115 ms respectively, within the existing 20-second cumulative bound.

## Discovered downstream constraint and remaining work

An intermediate test demonstrated that `HashAndHold` / `File.OpenRead` cannot
reopen these retained file objects with its ordinary sharing mode. A read-only
duplicate preserves the original native file object created with read/write
access; reducing the duplicate's access does not turn it into a fresh read-only
file open. Releasing and reopening would break custody, and loosening the held
file's sharing is not an acceptable replacement.

Accordingly this milestone deliberately ends at the original parent. Its fixture
verifies and disposes the output owner **before** the old path-based verification
and primary request begin. `ReleasedBeforePrimary=true` is explicit. The new
custody is not passed to the primary, and no diagnostic JSON is imported as a
capability. This is a verified parent boundary, not a full provenance handoff.

Next: bind the accepted output owner to the exact retained-source request and
carry native handles plus the required provenance into the original primary's
existing read path. Replace the corresponding old hash/read operation rather
than adding another pass. Keep the original evaluation owners and limits, and
test rejection and cleanup before closing parent custody. Existing native I/O
helpers may be reusable, but full-frontier app-manifest authority must not be
fabricated for this smaller fixture. Writer membership and exact witness-origin
proof remain separate obligations.

The first missing primary edge is still `file_writer_provenance_unproven`;
terminal integrity remains `unproven`, `GuiIntegrityOk=false`, and the primary
remains `FAIL_PROTOCOL_OR_INTEGRITY` / `fail`. `WriterCopyProvenanceProven`,
`PrimaryProvenanceHandoffProven`, `PrimaryFileIntegrityJoinProven` and
`WriterMembershipVerified` remain false. No historical outcome is reclassified.
The transfer is the existing 262,144-byte Regular-v4 loopback, not a live-256 or
canonical route-cycle proof.

## Development failures and coverage limits

The first four development tests failed before launch because the parent seam
matched multiple refresh calls; it is now anchored to the unique watchdog loop.
The next single-host run established the native handoff but exposed the ordinary
path-read sharing conflict above. These five failed attempts remain failed and
are retained separately from the final passing tests.

One rebuild was blocked by Avalonia's telemetry task attempting to write outside
the workspace. The final build disabled that optional task for this invocation
with `-p:UsedAvaloniaProducts=`; no project or user configuration was changed.
The guarded native loader also compiles independently on both PowerShell hosts.

Forced process death during duplication, malformed new-channel packets,
mid-copy cancellation, handle-loss injection, copy/capture/ack timeout injection
and nonempty-output injection have not been separately exercised on this new
path. Older generic GUI timeout/cancellation cases are not substitutes for them.
This is not a production security boundary or live-test readiness claim.

## Preservation

All 3,820 preceding pins matched before work. Nine previously dirty test files
changed; all 120 unrelated dirty files remain byte-for-byte unchanged. Three test
files and this document were added. The expected prior-pin changes are those
nine test files and the rebuilt test DLL. Production sources, processor, native
codecs, previous artifacts and the previous verification seal remain unchanged.

Final verification is recorded in
`artifacts/collector-outputs-20260925/FINAL-VERIFICATION.json`.
No aggregate suite, portable rebuild, real GUI/network test, full matrix, commit
or push was performed.
