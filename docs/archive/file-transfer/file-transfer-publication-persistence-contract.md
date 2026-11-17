# C1s: opt-in persistence of an adopted local publication

2026-09-14, branch `v0.7.0`, base HEAD `ef78e9d7`; accumulated work uncommitted.

## Scope and callsite

`Run-FileTransferRouteAcceptance.ps1` now accepts the default-off switch
`-CaptureLocalPublicationDiagnostics`. The top-level call passes it only to
non-fake Phase 5 scenario attempts. Within the Tuna workflow it applies only
to the normal `live-regular-activation-cycle` path. Each ordinary scenario
rerun gets its own invocation, scope and artifact directory. The internal
input-rewriting `v4-restart-v6-fallback` retry path, other routes, legacy/Phase 4
entrypoints, fake/dry-run entrypoints and direct Wrapper-owned analysis remain
outside this opt-in.

The workflow creates a fresh private output reference and captures run ID,
scenario ID and monotonic start timestamp before launching its Wrapper child.
An explicitly supplied `LocalPublication` reference keeps the old caller-owned
behavior and does not opt in to automatic persistence, even when empty. The
existing GUI relay, source ownership, two primary evaluations, original receipt
writer and one-shot adoption then run unchanged.

In the workflow's `finally`, only the genuine sealed `AdoptedPublication` type
may persist. Missing objects and caller-authored/deserialized lookalikes are
ignored without invoking their properties or methods. Private callsite locals
are not visible to child helpers. A failed retained proof or manifest check
still throws its original error; optional persistence cannot replace it.

## Persisted diagnostic and scope checks

The fixed filename is `filetransfer-local-publication-diagnostic.json` in the
exact current attempt's artifact directory. Its format is
`nlink-adopted-local-publication-v1`, with `authorityEffect=none`, the original
owned publication JSON, and immutable terminal receipt metadata: request ID,
owner, run/scenario IDs, receipt path, status, catalog digest and worker PID.
The original publication bytes are embedded without reinterpreting their
selected row, native-input descriptors, catalog or invocation identities.

Persistence requires `RouteAcceptance` ownership, matching captured run/scenario
IDs, the exact canonical receipt path under the requested directory, and an
adoption timestamp no earlier than this invocation's start. An old object from
before the attempt is rejected. The sealed type still has no public constructor
or setter; saved JSON cannot reconstruct it. These checks are trusted-callsite
custody, not authentication against malicious same-account code/private reflection.

Scope rejection does not consume a genuine object. After valid scope is admitted,
one interlocked write attempt is consumed, including on an I/O failure. There is
no retry or replacement. `CopyPayload()` and original receipt data are unchanged.

## Bounds and filesystem behavior

- Absolute drive-rooted paths only, at most 4,096 UTF-16 units; the drive must be
  fixed/local. At most 64 existing directory ancestors are checked, and reparse
  points are rejected. UNC, relative, missing or invalid directories omit output.
- The entire companion, including receipt metadata, is capped at 256 KiB by the
  existing fixed-schema canonical codec and a final byte-length check. This is
  stricter than admitting a 256-KiB payload plus unbounded metadata. No original
  input or report is reread, rehashed or reevaluated for this operation.
- The codec retains its existing 2,097,152 character/byte visit ceilings and
  a cooperative 250-ms budget. Checks bracket bounded serialization, ancestor
  checks and staging I/O. OS filesystem calls cannot be preempted by this budget;
  it is not a hard wall-clock or crash-durability guarantee. Original GUI and
  retained-analysis watchdogs are not extended.
- Write one uniquely named same-directory staging file with `CreateNew`, close
  it, then perform a non-overwriting `File.Move`. A final file or directory that
  already occupies the fixed destination is preserved. Failures omit output and
  attempt to delete only this invocation's successfully created staging file.
  Process termination or failed cleanup may leave an unadopted `.tmp`; it is not
  a diagnostic result. No primary file, existing companion or directory is deleted.

The companion is historical diagnostic data, not route authority, live proof,
peer causation or an acceptance verdict. File presence alone is insufficient;
later readers must bind its receipt/request/scope to the relevant attempt and
must not turn saved JSON back into an adopted capability. Existing destinations
can be stale and are deliberately not overwritten.

## Verification and next boundary

The real integration fixture exercises successful writes, existing-file and
directory-conflict failures, wrong/stale scope and replay on both PowerShell
hosts. It executes the complete controlled-offline Wrapper workflow, real primary
report writer and original terminal receipt, without synthetic selected failures
or reports. Saved bytes contain the exact primary-selected failure and receipt.
The original `FAIL_PROTOCOL_OR_INTEGRITY` verdict and live-proof rejection remain.
Separate workflow controls cover default-off, missing/copied objects, original
child/analysis errors, other routes and caller-owned output references.

Results: `artifacts/publication-persistence-20260914/RESULTS.md`.
Nine unique affected xUnit tests pass, including eight real persistence cases
and sixteen workflow controls across Windows PowerShell 5.1 and PowerShell 7.
No live GUI/NKN/256 MiB test, portable rebuild, commit or push is part of C1s.

The small offline producer-to-companion path is now implemented. The subsequent
[C1t pre-checkpoint review](file-transfer-publication-live-capacity-rca.md) found
that the latest live input exceeds source, writer and input-work bounds, so the
checkpoint/build/live-readiness gate is not approved. Revise and verify the
live-scale input/work contract before resuming that sequence. Absence remains a
valid fail-closed outcome, never a reason to select a later failure or weaken
authority checks. Stop before a new live run under the current authorization.

[C1u](file-transfer-publication-live-input-contract.md) now specifies that revision
with measured offline budgets. Its production implementation and live-shaped
positive gate are still pending; the small C1s tests do not replace that gate.
