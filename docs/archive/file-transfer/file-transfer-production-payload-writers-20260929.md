# Production terminal payload/native-writer association — 2026-09-29

## First missing connection

The production GUI-to-wrapper payload handoff existed, but its terminal
association with the original accepted native-writer custody was still compiled
only by the offline fixture. Both normal loaders reproduced
`Production native-writer payload take missing` before implementation.

The predecessor is `production-payload-wrapper-20260929/FINAL-VERIFICATION.json`,
SHA256 `8ac1a4144846c91045629a1719b18db06304bec376c8f7124040b796c55898c3`.
All 13,865 pinned files matched before edits. HEAD remains
`a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`; the baseline had 665
dirty entries. Before-images preserve eleven existing text files and five
pinned build outputs. No predecessor report or seal is edited.

## Bounded implementation

- The normal loader includes `LocalGuiWriterCanonicalPayloadWriters.cs`.
  `TryTakeWithWriters` takes only the original launch reports and original
  `FrontierParentCustody`; it cannot accept caller-authored writer facts or a
  reconstructed completion. `NativeWritersBound` is read-only.
- The implementation is moved from `FrontierAttemptPayloadWriters.cs`, whose
  duplicate fixture and loader entry are removed. The complete original file
  remains pinned under this phase's before-images. Test callers use the same
  production methods; parent/analyzer forwarding stays test-only.
- The sole terminal take requires the original current, already-taken and
  hash-verified payload pair. It invokes the original request's `TakeAfterExit`
  once, then requires reference identity with the exact completion accepted by
  the same native custody.
- Both writer tuples must match the original manifests: app request, native
  process and creation identity, writer ID, terminal drain sequence, and the
  corresponding original writer-facts sequence. Requests and writer IDs remain
  distinct.
- Only the original synchronous take may preserve payload pins across its
  legacy request-close event. After association, currentness depends on the
  original accepted native custody and terminal request scope. Native-custody
  retirement independently closes both duplicated payload handles. A rejected
  association retires the payload pair without retiring valid native custody.
- Production lifecycle methods replace the prior test-only partial hooks.
  Fault injection remains test-only. The existing work clock and 5-second
  allowance, pipe protocol and deadlines, and one-shot guards are unchanged.

This establishes a local evidence association, not route or runtime authority.
All live-proof, transfer-integrity, and publication-completion indicators remain
false. No coordinator/Core/transport path, route invariant, retry, progress,
throughput policy, or acceptance rule changed.

## Verification

The stabilized build passed with 339 existing warnings and zero errors. The
first green run passed four tests: both normal loaders and both valid native
writer associations. The focused lane passed 12/12: mismatched creation identity
on both hosts; mismatched app request, native process, writer ID, and terminal
sequence; ordinary payload custody on both hosts; and request-close/connected
request-disposal checks on both hosts.

The writer cases also reject null or copied inputs, repeated takes, and an
unverified payload pair without consuming the valid original take. Rejected
associations release both payload handles while preserving valid native custody.
Successful cases prove that retiring the original native custody closes payload
handles independently, while disposing a copied custody cannot retire them.

The affected compatibility lane passed 11/11: unchanged parent guard ordering,
both valid parent relays, parent-side foreign-writer rejection, normal wrapper
API checks, parent/analyzer API exclusion checks, and the retained canonical-source
analyzer consumer. The analyzer keeps its original three evaluations and one
native read and stops before route acceptance. Combined result: **27 distinct
passing offline tests**, with no failures or skips in the completed green runs.
The two intentional missing-API red results are retained, and the verifier
requires those same cases to have subsequently passed. No aggregate suite or
full GUI matrix was run.

The verifier checks every predecessor pin, exact dirty-file preservation,
before-images, build/test results, syntax, whitespace, the unchanged live entry,
and recorded child-process cleanup. It verifies the removed fixture against its
archived before-image and excludes only that obsolete source path from the new
pin set. Four passing listener tests separately assert original-process exit,
report drain, handle release, and zero idle work. Two added files and one removed
duplicate bring the dirty count to 666; HEAD and branch are unchanged.

## Next gate

Connect the authenticated wrapper-to-parent transfer-payload handoff using the
original accepted completion and writer-bound payload pair. Analyzer forwarding
and live execution remain gated; this association alone cannot enable them.

The live entry remains `Run-ScenarioFileTransferTunaHandoffFallback -Context $ctx`,
without canonical ownership enabled. No live NKN, live-256, full desktop GUI
matrix, portable package, commit, or push is part of this phase. The second
coordinator-published `file_tuna_v4` route is still mandatory for the live
route-cycle contract.
