# Native files: request-owned handoff into the original primary

2026-09-25, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Continues `file-transfer-native-request-binding-20260925.md`.
Offline test-only native adoption; no production or acceptance change.

## Implemented boundary

The original parent request's file association can now send its three protected
read-only file handles to its actual bound primary process. It checks the live
original request and the bound process ID/creation time before duplicating into
that process's native handle. A copied parent binding, an unbound target and a
repeated send are rejected.

The existing bounded three-file packet travels over the owned primary's stdin,
followed by a 32-byte digest of the original retained-source request's bounded
metadata body. That body includes the original source descriptor and serialized
accepted writer context. This hashes metadata, not transfer-file contents; it
does not turn a hash into authority. No named endpoint or JSON import is added.

The original attached source worker receives the files before its existing source
read. It verifies request/scope, actual target process identity, native file
identities/lengths and equality with its own original source/writer descriptor.
Only then does it acknowledge adoption. The parent releases its three handles
and verifies that write-open attempts still fail with Windows sharing violation
32 for every file. The C# producer already retired its handles during the first
hop, so the actual primary now protects these three files. A release acknowledgement
lets the primary continue; missing release, closed request or lost native handles
cannot supply current ownership. Producer-side hashes remain labelled as such.

At the existing after-reports observation point, native ownership is checked
against the actual source worker, completed source reader, original primary
custody, completed full/compact pair, input context, arrays and result views.
Nothing is reevaluated or reread from a log by this probe. The original source
cleanup disposes the adopted handles; post-run C# assertions verify release and
independently corroborate file identity, size and hashes. These exported
observations remain outputs, never analyzer input capabilities.

## Verification

All **12 distinct targeted tests passed**, without failures or skips:

- `artifacts/native-primary-files-20260925/results/native-primary-focused.trx`:
  two positive hosts and three rejection integrations passed initially. The
  foreign-request, mismatched-original-descriptor and truncated-packet cases
  remain the accepted negative results; each records zero authority evaluations,
  no adoption acknowledgement, failed child exit and released handles.
- `artifacts/native-primary-files-20260925/results/native-primary-contract.trx`:
  seven unchanged-path regressions passed: both parent-request integrations,
  both native-parent integrations, the five-case first-hop packet rejection test,
  and both ordinary original-primary integrations. The relevant lane ran once.
- `artifacts/native-primary-files-20260925/results/native-primary-sharing-check.trx`:
  both positive hosts were rerun after final review required sharing violation 32
  specifically, instead of accepting any I/O exception as write protection. Those
  two results supersede the initial positives. The change affects only the
  runtime-compiled test partial on this positive handoff path; unrelated tests
  were not rerun and the net8 test assembly did not require rebuilding.

Each positive primary rejects nine substitutions: copied file owner, copied
source worker, copied source reader, copied context, absent evaluation pair,
cloned full array, cloned compact array, absent full view and copied custody.
Disposing a copied file owner leaves the original valid. Repeated receive is
rejected before another input read. The 17 parent-request controls and six
existing primary custody controls remain in place.

Both positive hosts preserve exactly three original authority evaluations,
93 full / 16 compact records, the exact two real terminal rows and unchanged
original result views. Diagnostic work remains within the original cumulative
20-second limit. `GuiCompleted=true`, `GuiIntegrityOk=false`, primary
`FAIL_PROTOCOL_OR_INTEGRITY` / `fail`, and the unproven terminal-integrity gate
are unchanged. No budget, route, throughput or acceptance rule was relaxed.

## Honest result and next edge

The opt-in diagnostic now reports
`PrimaryNativeFileAdoptionProven=true` and
`NativeFileStatus=verified_offline_original_primary_native_files_only`.
Its first missing edge advances from `file_owner_handoff_unavailable` to
`file_writer_provenance_unproven`. Non-handoff regressions retain the old edge.

This proves native file custody joined to the original request/evaluation
objects. It does **not** prove that the collector's copied log came through an
owned provenance chain from the protected original producer log. It also does
not establish owned writer membership or a complete file-integrity join:
`WriterCopyProvenanceProven=false`, `WriterMembershipVerified=false`, and
`PrimaryFileIntegrityJoinProven=false`. Existing parent reports likewise keep
the broader `PrimaryFileOwnerHandoffProven=false` rather than silently broadening
their earlier meaning.

Next is the original-log-to-collector/writer provenance edge. Establish it in
the actual owned collection/read lifecycle, before writer completion. Do not
substitute a later path/hash comparison, cross-attempt JSON correlation or
observer-side log replay. Membership must reuse or establish an owned result
within the existing single-reader lifecycle; no invented failure witness or
second authority evaluation. Canonical live-256 scope, the complete five-route
cycle and independent failures remain separate obligations.

Producer/parent process death during adoption, explicit cancellation/timeouts,
lost release acknowledgements and post-adoption forced handle loss have not been
independently fault-injected in this milestone. Normal acknowledged release,
repeated use, malformed input, custody substitution and cleanup were tested.
This remains a test-only prototype, not a production security API or live-test
readiness claim.

## Preservation

All 1,326 previous pins matched before work. Ten previously dirty test files and
one previously clean test worker wrapper changed. Three test files and this
document were added. All 105 unrelated baseline dirty files are byte-for-byte
preserved. Of the prior pins, 1,315 are unchanged; the expected 11 changes are
those ten already-dirty test files and the rebuilt test DLL. Historical evidence,
production sources, production loaders and codecs remain unchanged.

PowerShell parsing, `git diff --check` and touched-file whitespace checks pass.
The verification seal is `artifacts/native-primary-files-20260925/FINAL-VERIFICATION.json`.
No aggregate suite, portable rebuild, live run, full GUI matrix, commit or push
was performed.
