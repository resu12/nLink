# Collector output: owned read by the original finalizer

2026-09-25, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Continues `file-transfer-native-collector-read-20260925.md`.
Offline, test-only; production and acceptance remain unchanged.

## Implemented boundary

The previous collector copied through the protected producer-log handle but
closed its output before the finalizer reopened it by path. The new opt-in
fixture retains that exact output handle from the copy through finalization.
It keeps the native identity and byte count, counts LF boundaries during the
existing copy, requires the finalized LF tail and limits this fixture to bookmark
zero. The original 4-MiB input limit and the stream processor's bounds remain.

The private output stream retains `FileShare.Read`, denying competing writes
and replacement. Its read-only wrapper exposes neither writing nor seeking.
The original finalizer, after its normal one-shot attachment, can claim that
wrapper only with matching original collector object, request/channel/scope,
output path and copied line count. Copied finalizers and collectors cannot claim
or retire the original owner.

The actual finalizer's test-only processing seam calls the existing
`ProcessFromStream` implementation once. The production processor is unchanged.
Its original stream capture must own the exact retained wrapper. The same
result's original native-output factory then creates the bounded native capture
from the processor's actual output facts, without a second source scan, content
hash pass or authority evaluation. Its 250-ms capture clock starts before
processing, so that bound is stricter, not relaxed.

The existing finalizer validation, semantic-copy checks and original `Complete`
call still apply. Immediately before that call, the exact original finalizer,
collector, streaming result and native capture must match. The original
finalizer's cleanup retires the stream capture and collector output; the normal
successful-child-exit requirement still gates parent completion adoption.
No issuer is attached early or exposed in the diagnostic result.

## Verification

All **nine targeted tests passed**, with no failures or skips:

- `artifacts/collector-finalizer-20260925/results/collector-finalizer-focused.trx`:
  the new Windows PowerShell and PowerShell 7 integrations.
- `artifacts/collector-finalizer-20260925/results/collector-finalizer-contract.trx`:
  the five existing native-collector integrations and both original GUI
  parent/finalizer fixture tests, with 25 cases per host. This lane ran once.

Each new integration rejects four pre-read substitutions (copied collector,
copied issuer, foreign source path and incorrect frozen line count), then rejects
five capture substitutions (copied collector, copied issuer, copied result,
copied native capture and absent capture). Repeated processing is rejected before
another source read. Disposing copied owners leaves the original valid.

Actual competing write-open and rename attempts fail with Windows sharing
violation 32 while the copied output is retained. After original finalization,
both the collector and joined capture are inactive and write-open succeeds.
The original parent accepts the actual successful GUI completion and the original
primary still executes normally; diagnostic JSON is only a post-run observation.

Windows PowerShell attempt `372ab3c9-6cb7-433a-8c18-d935bd9865ae` copied 164,646
bytes and retained 92 full / 16 compact primary records, with 96 ms diagnostic
work. PowerShell 7 attempt `1fcad250-67f7-495b-ab91-fe57a632f4c7` copied 166,165
bytes and retained 93 full / 16 compact records, with 117 ms diagnostic work.
Both preserve three original authority evaluations, both real terminal rows,
unchanged original primary result views and the 20-second cumulative budget.

Final review corrected two comments and removed one unused import after the
focused run; these do not change behavior. The guarded loader compiles on both
hosts after those edits. PowerShell parsing, touched-file whitespace checks and
`git diff --check` pass.

## Remaining boundary

The child-local diagnostic now proves `FinalizerOwnedStreamCapture=true` and
`FinalizerOwnedNativeCapture=true`. It does **not** prove that this association
survives the original GUI completion and subsequent primary handoff:
`PrimaryProvenanceHandoffProven=false`, `WriterCopyProvenanceProven=false` and
`WriterMembershipVerified=false`. The primary's first missing edge remains
`file_writer_provenance_unproven`; terminal integrity remains `unproven`.

`GuiCompleted=true`, `GuiIntegrityOk=false` and primary
`FAIL_PROTOCOL_OR_INTEGRITY` / `fail` remain. No historical result is reclassified.
The real transfer in this fixture is only the existing 262,144-byte Regular-v4
loopback, not a live-256 or complete route-cycle proof.

Next: carry the owned collector/finalizer association, including the required
processor-output custody, through the original successful writer completion
into the exact retained-source request and primary. The current ordinary output
files are not newly held across that boundary by this change. Do not substitute
serialized booleans, paths, hashes, matching IDs or this diagnostic JSON for
owned provenance. Keep the original successful-exit and cleanup gates; do not
keep the GUI alive to bypass them. Writer membership and canonical scenario
scope remain separate obligations, without invented witnesses or another
authority evaluation.

Forced process death, cancellation during this owned read, native handle loss,
capture-timeout injection and nonempty-copy-target injection were not separately
tested on the new path. Existing generic GUI cancellation and timeout checks are
not substitutes for those cases. This is not a production security boundary or
a live-test readiness claim.

## Preservation

All 2,910 previous pins matched before work. Eight previously dirty test files
changed; all 117 unrelated baseline dirty files remain byte-for-byte unchanged.
Three test files and this document were added. Of the prior pins, 2,901 are
unchanged; the expected nine changes are those eight test files and the rebuilt
test DLL. Production sources, processor, loaders, codecs, prior artifacts and
the preceding verification seal are unchanged.

The new seal is `artifacts/collector-finalizer-20260925/FINAL-VERIFICATION.json`.
No aggregate suite, portable rebuild, real GUI/network run, full GUI matrix,
commit or push was performed.
