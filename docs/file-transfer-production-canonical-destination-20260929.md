# Production canonical destination adoption — 2026-09-29

## First missing connection

The receiver already retained a native read-only handle after Core completion,
but the GUI adopter existed only in the explicit test build. Its test entry
accepted caller-authored byte counts and hashes. The normal loader had no
source-bound destination adoption API. A loader regression failed with
`Production destination handoff missing` before implementation.

The predecessor is `production-native-picker-20260929/FINAL-VERIFICATION.json`,
SHA256 `9b88330f7fee49717f699ebf0867ed8d65b4e70e560f50ffdae66581e741b5a8`.
All 12,154 pins matched before edits. HEAD remains
`a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`; the baseline had 655
dirty entries. Before-images preserve the nine edited text files and five
potentially changed pinned build outputs.

## Bounded implementation

- The normal loader now includes `CanonicalAttemptDestination` and
  `TryAdoptDestination(request, expectedSource, cancellationToken)`. The public
  entry accepts neither a path nor a caller-authored size, hash, or native handle.
- Admission requires the original consumed dispatch, its exact held sender
  source, a prior source-delivery attempt, and the exact original helper launch.
  Expected size is 256 MiB and expected SHA comes from protected source custody.
  A delivery attempt is not asserted to prove sender reads or route acceptance.
- The existing bounded pipe protocol is moved, not duplicated. It checks native
  client PID, request ID, process creation, file identity and size; duplicates
  only read access; hashes the held destination; and requires nonce commit.
  The packet cap remains 4,096 bytes and the existing 75-second adopter ceiling
  remains unchanged. Caller cancellation is linked to that ceiling.
- Source custody is rechecked before acknowledgment and before retaining the
  committed result. It also remains a condition of destination validity after
  adoption. Rejection after admission spends the one-shot attempt and disposes
  any provisional duplicate. Pre-canceled or foreign calls do not consume the
  original attempt. Copy disposal cannot retire original custody.
- Dispatch disposal now releases the destination in production. Normal helper
  exit and original output/attachment completion remain required after exit.
- Small-file expectations, fault injection, and closed-writer association stay
  in the test-only partial. Downstream test references use the shared native
  implementation; no production writer/payload forwarding is added.

During review, the original-dispatch check was placed before source validation:
a rejected copied dispatch must not retire the original source as a side effect.
Focused tests exercise that ordering and verify that a subsequent valid call
still succeeds.

Adoption is provisional evidence custody only. Live-proof eligibility, transfer
integrity authority, and publication completion remain false. No coordinator,
route invariant, acceptance rule, retry behavior, throughput policy, diagnostic
limit, or live-test budget changed.

## Verification

The test-project build passed with 339 warnings and zero errors. Both normal
PowerShell loaders compile the production adopter without exposing test-only
adoption or writer-binding APIs.

All six focused tests passed (3m14s). They include same-attempt 256-MiB native
destination adoption on both hosts, copied/foreign/repeated requests, a
pre-canceled call followed by valid adoption, copied custody disposal, and
invalidation/release when the original source or attempt is retired. The actual
offline sender/receiver outputs must agree on the full-size SHA.

All ten affected compatibility tests passed (6m23s): the seven existing small
destination handoff cases on PowerShell 7, the valid case on Windows PowerShell,
full-size offline NKN-carrier fallback adoption, and the original writer/payload
handoff through the retained analyzer. That analyzer case deliberately stops
before route acceptance; its success does not establish live correctness.
The combined result is **16 distinct passing tests**, with no failures or skips.
No aggregate suite or full GUI matrix was run.

The phase adds three files, bringing the dirty count to 658. Its final verifier
checks prior-file preservation, before-images, exact dirty-file changes, syntax,
whitespace, the unchanged top-level live call, and recorded child-process
cleanup, including the primary analyzer process. Results and current file hashes
are recorded in the phase's new verification seal.

## Next gate

The GUI adopter is production-capable, but the receiver's evidence request and
pipe handoff are still invoked explicitly by offline fixtures. Connect those
calls to the original application lifetime next, without granting new route or
completion authority. Then join the remaining closed-writer/payload forwarding
to the same original attempt before enabling live execution.

The top-level live call remains
`Run-ScenarioFileTransferTunaHandoffFallback -Context $ctx`, without canonical
ownership enabled. No live NKN, live-256, desktop GUI matrix, portable package,
commit, or push was performed.
