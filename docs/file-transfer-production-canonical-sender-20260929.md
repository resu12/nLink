# Production canonical native sender handoff — 2026-09-29

## Scope and first missing edge

The protected source checkpoint retained the original file, but the native
sender packet and redirected sender stdin existed only in the explicit test
profile. This phase shares that channel between the normal GUI loader and Core,
and connects delivery at the original pre-Send boundary. It does not yet connect
the real app picker to the receiver or provide a production runtime-read factory.

Baseline: `production-canonical-source-20260929/FINAL-VERIFICATION.json`, SHA256
`962c4d3042bced73f393210fb8ad15540426104eb5631ba8b7634822b047543c`.
All 11,036 baseline pins matched before implementation; HEAD remained
`a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`, with 646 existing dirty
entries. Before-images preserve seven edited text files and five rebuilt pinned
binaries. Existing unrelated work is outside the change allowlist.

## Bounded implementation

- `LocalCanonicalSenderSource.cs` supplies one native packet implementation to
  the PowerShell GUI loader and Core. The bounded 512-byte packet, duplicated
  read-only handle, original request/PID/creation checks, exact file identity,
  and 268,435,456-byte source requirement retain the existing fixture contract.
- The admitted original canonical helpee launch owns redirected stdin. Ordinary
  launches and the helper are unchanged. The test profile no longer injects
  sender stdin; it only retains its separate cross-process helper channel.
- `CanonicalAttemptSource.TrySendToHelpee` requires the original held source,
  original helpee request and current launch, and spends a one-shot delivery
  claim. It opens a verified read from the held source, never a replacement path.
  The pre-Send hash branch attempts delivery after acquiring that source.
- Delivery refusal does not convert the existing SHA into an error or grant
  proof. Optional source omission retains ordinary hashing/scenario behavior.
  A published remote handle is never closed by its numeric value in the sender:
  child exit bounds failed delivery without risking handle-reuse corruption.
- The receiver is non-constructible publicly, accepts only its original current
  app lease, and permits one read claim. Copies, stale leases, repeated claims,
  wrong native file identity, and closed custody are rejected. Packet metadata
  and a successful write are evidence, not route/runtime or transfer authority.
- The old test adapter now invokes this production receiver and sender. Fault
  mutation and the existing coordinator-token-checked runtime-read fixture remain
  test-only; they have not been promoted into production retry/read authority.

No coordinator behavior, route protocol, acceptance threshold, performance
setting, timeout, retry cadence, or diagnostic limit changed.

## Verification

The normal-loader regression first failed on both PowerShell hosts with
`Production sender handoff missing`. The final test-project build succeeded
with 356 warnings and zero errors; the shared native channel has no warnings
in that build. Intermediate platform warnings were removed by explicit Windows
guards around native entry points, without changing Windows packet behavior.

All 18 focused checks passed with no skips (6m07s): two new normal-loader checks,
12 existing sender/loader cases, and four current-runtime read cases. Coverage
includes original service metadata hashing, request/creation/file mismatches,
closed handles, and runtime retirement/disposal. Embedded controls exercise the
production receiver itself with copied source and lease objects, repeated claims,
and copied disposal; invalid copies cannot consume or retire the original.

The one affected compatibility lane passed all 26 cases with no skips (8m59s):
ten source lifetime/default cases, twelve canonical role launch/failure cases,
and four ordinary valid/default launches. The final total is **44 distinct green
tests**, with no remaining failures. No aggregate suite, desktop automation, or
live NKN traffic was involved.

Four new repository files plus the newly modified Core project bring the dirty
entry count to 651. Preservation, syntax, whitespace, exact live-entry and recorded
child-process checks are enforced by the phase verifier and recorded in its seal.

## Remaining integration boundary

The real top-level call remains
`Run-ScenarioFileTransferTunaHandoffFallback -Context $ctx`; it supplies neither
owner nor canonical attempt. The actual app picker still opens its ordinary
path-based source. Only the test host currently consumes the shared native
receiver. Thus this result proves the bounded launch/delivery primitive and its
receiver checks, not end-to-end live app consumption or transfer completion.

Next, connect native receiver consumption at the original app lifetime/picker
boundary with bounded cancellation and a coordinator-issued runtime-read owner.
Metadata and successor payload reads must use retained native custody, invalidate
stale views, and never derive authority from packet fields or route observations.
Then complete destination and writer/payload custody before enabling the live
entry. A fresh portable build and strict preflight remain later gates.

No portable package rebuild, live-256 run, full GUI matrix, commit, or push was
performed in this phase.
