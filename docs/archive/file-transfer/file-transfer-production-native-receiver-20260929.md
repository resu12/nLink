# Production native receiver lifetime — 2026-09-29

## First missing connection

The GUI had a production source-bound destination adopter, but receiver evidence
capture and pipe handoff still depended on explicit calls from offline fixtures.
The original application lifetime did not request, start, cancel, or join that
handoff. The new regression initially failed because the canonical helper did
not receive the destination opt-in (`Expected: "1"`, `Actual: null`).

The predecessor is `production-canonical-destination-20260929/FINAL-VERIFICATION.json`,
SHA256 `d2a1d830181a9627509f8c7630b44819121248078b8911ef936d329ae2cd17ee`.
All 12,498 pins matched before edits. HEAD remains
`a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`; the baseline had 658
dirty entries. Before-images preserve the nine edited text files and five
potentially changed pinned build outputs.

## Bounded implementation

- The canonical launcher opts in only its original helper with
  `NLINK_APP_CANONICAL_DESTINATION_V1=1`. This marker requests evidence handling;
  the original native app lease, service, destination, and finalization source
  remain necessary. The marker alone supplies no authority.
- After integrity verification, the shared receive-finalization path requests
  evidence before destination finalization. It checks the exact current context
  and finalization source before and after native admission. Native lease checks
  execute outside the service gate. Ordinary unmarked receives do not schedule
  this work or retain a native evidence hold.
- The existing terminal transition still determines completion and transfers
  the guard-owned native read hold. Only its successful, current completion can
  start the app-owned handoff task. The worker starts outside the service gate
  and reuses the existing native GUI identity/nonce protocol; it does not reopen
  the destination path or manufacture an external receipt.
- Pending evidence cannot consume the app's one-shot handoff slot. Copied app
  owners, copied evidence, and repeated calls cannot replace the original task
  or retire its custody. A failed claimed attempt disposes its own hold.
- Connection and exchange share the existing ten-second handoff ceiling. App
  shutdown cancels and joins the worker outside the admission/service gates,
  before the original final log drain and release. Optional handoff failure
  does not change the transfer result. The ordinary final drain remains singular.
- Legacy explicit test calls remain available. New automatic profiles read the
  production-created evidence and await the production-owned task rather than
  requesting evidence or opening the GUI pipe themselves.

The final review added the managed completed-evidence check so an early request
cannot spend the one-shot slot. Its regression exercises early, copied, valid,
and repeated requests in the same original lifetime.

This is evidence custody, not a route decision or an acceptance result. No
coordinator decision, route invariant, retry behavior, progress publication,
throughput policy, diagnostic limit, or live-test budget changed. Closed-writer
and payload forwarding are not enabled in this phase.

## Verification

The production rebuild passed with 358 warnings and zero errors; the final
fixture-only incremental build passed with 339 warnings and zero errors. Nine focused offline
tests passed (5m05s): automatic GUI handoff on both PowerShell hosts, missing
listener cancellation/join at app shutdown, pending disposal, stale destination,
stale session, cancellation, and full-size 256-MiB Regular/fallback transfers.
These use deterministic app-role processes and the existing native custody
fixtures, not a live network or interactive desktop run. The small-file cases
use the test-only adopter expectations; production adoption remains 256 MiB.

After the final guard changes, one affected compatibility lane passed all 16
tests (1m31s): six stale-finalization-source cases, three ordinary destination
completion/accessibility cases, legacy manual receiver capture, four app
shutdown/setup cases, native sender runtime compatibility, and the automatic
GUI case with the new early/copy/repeat assertions. One case overlaps the
focused run. Final call-site review also caught a fixture lifetime assertion
preceding existing incompatible-profile rejection. All eleven exact rejection
cases reproduced that regression, then passed after moving the assertion below
the existing guards. The combined result is **35 distinct passing tests**, with
no failures or skips in the completed green runs. No aggregate suite or full
GUI matrix was run.

An intermediate build exposed a missing fixture-local `lifetime` variable,
which was corrected. A test run inadvertently started against the prior build
was canceled immediately and produced no TRX; it is excluded from verification.
Its artifacts and both intentional red runs are retained, with recorded
children checked for exit. Neither incomplete run is counted as passing.

The phase adds three files, bringing the dirty count to 661. The final verifier
checks prior-file preservation, before-images, exact dirty changes, test counts,
PowerShell syntax, whitespace, the unchanged top-level live call, and recorded
child-process cleanup. Results and file hashes are captured in a new seal;
predecessor seals and reports are not edited.

## Next gate

Join the remaining closed-writer/payload forwarding to the same original attempt.
Then complete the reviewed checkpoint, portable rebuild, and strict preflight
before enabling targeted live execution. Do not infer live correctness from
the offline SHA or fallback completion; the route-cycle contract still requires
a second coordinator-published `file_tuna_v4` route.

The top-level live call remains
`Run-ScenarioFileTransferTunaHandoffFallback -Context $ctx`, without canonical
ownership enabled. No live NKN, live-256, desktop GUI matrix, portable package,
commit, or push was performed.
