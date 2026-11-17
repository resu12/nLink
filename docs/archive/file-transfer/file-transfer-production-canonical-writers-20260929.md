# Production canonical closed-writer association — 2026-09-29

## First missing connection

Native source and destination custody existed in production, including the
receiver's automatic app-lifetime handoff. However, the role-to-closed-writer
join and both payload/writer associations were compiled only by the explicit
offline test loader. The normal loaders on Windows PowerShell and PowerShell 7
both reproduced `Production closed-writer join missing`.

The predecessor is `production-native-receiver-20260929/FINAL-VERIFICATION.json`,
SHA256 `9be78661e0e1aaf7367b6d03b4beb436877d657c106909fc72a918c162193de6`.
All 12,837 pins matched before edits. HEAD remains
`a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`; the baseline had 661
dirty entries. Eighteen before-images preserve candidate source and build files;
the final change allowlist is limited to eleven existing text files and five
potentially changed pinned build outputs.

## Bounded implementation

- The normal loader includes `LocalGuiWriterCanonicalWriters.cs`, exposing
  `CanonicalAttemptWriters` and `TryBindRoleWriters(binding, match)`. The join
  accepts original objects, not caller-authored manifests or writer metadata.
- The implementation is moved from the existing fixture, not duplicated. It
  requires the original consumed attempt, original finalizer binding and writer
  match, both original role requests/reports, successful app shutdown and output
  drain, exact native request/process/creation identities, and the two distinct
  writers with their matched terminal sequences.
- Source and destination now expose `TryBindClosedWriters` with the same original
  writer object. Both associations remain one-shot and retain their existing
  reference-identity and lifetime checks. Association does not reopen either
  payload or export a native handle.
- Copied, foreign, repeated, mismatched, canceled, and retired objects remain
  rejected. Once associated, a payload's currentness still depends on the
  original writer join; retirement cannot revive that join or reissue it.
- Test call sites use the production methods directly. Only diagnostic logging,
  fault injection, small-file expectations, and downstream test-only forwarding
  remain in the fixture partials. The normal build does not include the
  diagnostic implementation. A loader check caught and corrected the partial
  diagnostic hook's initial visibility error before end-to-end verification.

All authority indicators remain false. This establishes a local association
with already-closed writers, not transfer integrity, route acceptance, native
writer publication permission, or a completed parent/analyzer handoff. No Core
runtime path, coordinator decision, route invariant, limit, timeout, retry,
progress, or throughput policy changed.

## Verification

The stabilized test build passed with 339 warnings and zero errors. Both normal
loader checks and the valid original-writer case on both hosts passed (four
tests, 49s). Twelve further focused tests passed (6m08s): reverse app adoption,
wrong app/terminal sequence, parent cancellation, closed output, spent budget,
four source-binding lifetime cases, and automatic 256-MiB Regular/fallback
receiver paths.

The seven affected compatibility tests passed (4m11s): production source and
destination loader checks on both hosts, valid/foreign native-writer payload
handoffs on PowerShell 7, and the existing retained analyzer consumer. That
consumer deliberately stops before route acceptance and retains its original
three evaluations and one native read. Combined result: **23 distinct passing
offline tests**, with no failures or skips in the completed green runs. The
intentional missing-API red run and the corrected loader visibility failure are
retained separately; the verifier requires those same cases to pass afterward.
No aggregate suite or full GUI matrix was run.

The verifier checks all predecessor pins, exact dirty-file preservation,
before-images, syntax, whitespace, test outcomes, the unchanged live entry,
and recorded child-process cleanup. No predecessor report or seal is edited.
The phase adds two files, bringing the dirty count to 663.

## Next gate

Connect the authenticated GUI-to-wrapper transfer-payload handoff using the
same original bound source, destination, and closed writers. Parent/analyzer
payload forwarding remains test-only; do not enable live execution from this
local association alone. A reviewed checkpoint, rebuilt portable package, and
strict preflight still precede a targeted live run.

The top-level live call remains
`Run-ScenarioFileTransferTunaHandoffFallback -Context $ctx`, without canonical
ownership enabled. No live NKN, live-256, desktop GUI matrix, portable package,
commit, or push was performed. The second coordinator-published `file_tuna_v4`
route remains mandatory for the live route-cycle contract.
