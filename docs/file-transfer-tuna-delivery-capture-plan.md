# Plan: one bounded Tuna delivery trace

Status: Gate A implementation authorized by the user's `implement`, 2026-09-10.
Baseline `057850f3`. Gate A is bounded to 45 active engineering minutes, one agent.
Gate B was separately approved by the user's `approved proceed`, 2026-09-10:
scoped commit, canonical package build/freeze and exactly one additional strict
live attempt, including a failed preflight, under the limits below.

## Gate A implementation notes

The implementation uses `NLINK_TUNA_DELIVERY_TRACE=1` plus
`NLINK_TUNA_DELIVERY_CAPTURE_ID=<fresh GUID in D format>`, shared by both app
roles and inherited by their children. Neither ordinary file-transfer tracing
nor xUnit enables it. Set these before starting the apps; the .NET producer is
fixed for that process. This is configuration documentation, not run approval.

Private diagnostic scopes bind a selected sidecar frame to its exact send call
and raw receive callback. The application disposition captures that immutable
binding before asynchronous dispatch. No public interface or authority token
changes. Go observations retain direction, exact sequence, occurrence and wire
hash; both stdout adapters whitelist them with their captured process owner's
run ID, including late diagnostic output from an old listener owner.

Limits remain 64 bulk Data frames per direction/connection, four connections,
4096 records and 4 MiB of serialized trace messages per producer (excluding the
existing operational-log wrapper), with marker reserve. Missing stages, cap
markers, write failures, malformed fields and logger drain/gap failures remain
incomplete evidence. Connection start and direction-end records explicitly say
`trace_tail=unproved`; an end is not an output-drain acknowledgement. Concurrent
queue acceptance logging can follow write logging; record order alone is not
transport authority. Repeated wire hashes require occurrence/connection evidence
and an unambiguous join, never a proximity-based association.

The full-log retention control uses the existing streaming copy/filter only,
not an evaluator or acceptance report. Its canonical line representation has
separators between lines and no final newline. Original and canonical capture
hashes must be pinned separately when their newline representation differs.

Verification receipts and bounded closeout are recorded locally in
`artifacts/validation/consolidation-bounded-20260907/tuna-delivery-trace/RESULTS.md`.
Gate B requires review of these changes and the remaining limitations below.

## Objective and limits

For one freshly observed batch, connect the existing data-path send record to
local IPC writing, both Go bridge endpoints, receiver IPC reading and the
existing application receive record. Find the last positively observed stage
and the first unresolved edge. A fully localized loss is a goal, not guaranteed
by another run; forced termination may leave the final stage unprovable.

Historical helper9564 remains unresolved. The loopback fixture at057850f3 proves
that a completed local write does not imply peer receipt, not that the historical
Tuna batch was lost at a particular hop. The second-off payload wait was explicitly
skipped in that campaign. Do not change that fault timing to make this run pass.

This is transport observation, not the parked `proven_stale_rejected` classifier.
No Coordinator/route authority, replay/security policy, wire protocol, acceptance
rule, mandatory evidence catalog or primary GUI/report predicate changes.

## Proposed capture

Use a separate, default-off diagnostic opt-in, provisionally
`NLINK_TUNA_DELIVERY_TRACE=1`, with a fresh capture ID shared by the two app roles
and inherited by their sidecars. It must not be enabled merely by running tests
or by the broader existing `NLINK_FILETRANSFER_TRACE` flag.

Join exact wire bytes, not range, byte length, outer MessageId alone, timestamps
or equal local counters. Proposed identity:

```text
ftwire1- + lowercase full SHA256(
  UTF8("nLink/tuna-delivery/wire/v1\0") || exact serialized outer-envelope bytes)
```

Here `\0` is one NUL byte. The Go sidecar Data payload already contains those
same outer-envelope bytes; exclude the sidecar header. Compute from the actual
bytes being sent/read, never from a reconstructed envelope. Use identical C#/Go
test vectors. Log only the hash, byte count and bounded metadata: never plaintext,
keys, wallet details, raw peer identities or envelope payloads.

Each record identifies capture, app role/writer, local client or Go process and
connection instance, direction, stage, original sidecar sequence when available,
and wire hash. Retain existing stop reasons and monotonic per-producer ordering.
Sequence is connection-scoped; a replacement connection must not inherit the old
identity. Bind child records to the actual owning process/connection, not a latest
mutable client or PID alone. Repeated exact bytes remain distinct occurrences;
ambiguous cross-connection matches remain unresolved. This is not an authenticated
session-incarnation or route-publication proof.

| Boundary | Required positive observation |
|---|---|
| Existing batch send | Exact wire hash adjacent to existing range/frame and captured publication fields; existing send-result meaning unchanged |
| .NET sidecar queue | Accepted original frame sequence and hash, captured from that frame rather than rereading a counter |
| .NET local write | Write entered and successfully completed for that same frame; failure/cancellation is separate |
| Sender Go bridge | Full app-IPC frame read; Tuna write entered/completed with preserved sequence/hash |
| Receiver Go bridge | Full Tuna frame read; app-IPC write entered/completed with preserved sequence/hash |
| Receiver .NET | Full IPC Data frame read before the availability guard; callback entered or rejected as unavailable |
| Application boundary | Existing data-path receive/disposition, joined only through an unambiguous observed envelope-to-wire association |

The raw receiver boundary must precede decryption/dispatch so an application
rejection cannot look like network non-arrival. Preserve existing outer-ID
fingerprints as additional correlation fields. If an outer-ID association is
ambiguous, or IPC receipt has no uniquely bound application record, report that
edge unresolved; do not broaden this phase into a receive/replay redesign.

### Bounded collection and preservation

- Trace only the first64 bulk Data frames per direction per connection, for at
  most4 connections per producer. Both activation cycles remain eligible; do not
  switch tracing off based on a mutable current route during teardown.
  Transport batch mappings must use that same selected-frame window and immutable
  call/frame identity, not an independent all-send counter that Regular traffic
  could exhaust first. If this needs an authority/interface redesign, stop Gate A.
- Cap each producer at4096 records and4MiB of serialized trace data, reserving
  capacity for start/end/overflow markers. These are diagnostic caps only, not
  transport queue, timeout or connection limits. Any cap/drop/parse failure marks
  that trace incomplete. Never silently truncate and claim a complete interval.
- Capture .NET and Go records through the existing logging/process-output paths.
  Both listener and dialer stdout handlers currently retain only event/reason
  for ordinary bridge events; they must preserve a strict whitelist of the new
  trace fields, with actual owner identity. Do not copy arbitrary child JSON or
  assign authority to a trace event.
- Retain the full original logs separately from compact/catalog-selected evidence.
  Pin input hashes, writer/process identities, trace start/end, frame-window caps,
  record counts and capture errors. A forced kill without completed output drain
  prevents a negative claim about the missing tail. Do not extend stop/drain waits.
- No new per-frame disk flush, blocking acknowledgment, persistent service or
  whole-log reevaluation. Disabled tracing must add no hashing/emission. Enabled
  tracing can perturb timing; document that limitation and make no performance
  improvement or production repeatability claim from this experiment.

Proposed implementation touches are the .NET sidecar client, narrow transport
trace mapping, the two existing child-output adapters, and Go `bridgeDirection`,
plus focused tests. Reuse a small private helper for opt-in/bounds/hash formatting;
do not introduce public DTOs or a general evidence/authority framework.

Source anchors: [queue/write/receive](<../src/nLink.Infra.Nkn/NknTunaSidecarClient.cs#L129>),
[Go forwarding](<../tools/nkn-tuna-sidecar/main.go#L1258>),
[dialer output](<../src/nLink.Infra.Nkn/NknTunaAccelerationLane.cs#L404>),
[listener output](<../src/nLink.Infra.Nkn/NknTunaListenerSidecarSupervisor.cs#L496>),
[unchanged off-action branch](<../tools/GuiSmoke-Windows.ps1#L36494>).

## Gate A: separately approve instrumentation and focused tests

Proposed bound: at most45 cumulative active engineering minutes, one agent,
including implementation, review and write-up; unattended command time recorded
separately. Stop at the cap or if collection requires lifecycle/orchestration
redesign. This approval would not authorize a commit, package build or live run.

Required checks before presenting a go/no-go:

1. C#/Go exact hash vectors, including changed wire bytes under a reused outer
   MessageId. No cryptographic/session-authority claim is added.
2. Extend the existing loopback forwarding/stopped controls to verify exact
   trace stages, preserved bytes and zero peer receipt after joined shutdown.
   Add in-memory/local Go bridge read/write success and write-failure controls;
   no NKN or paid sidecar test as an implicit dependency.
3. Disabled trace, per-connection restart/sequence reuse, cap/overflow, malformed
   child trace and abrupt-stop incompleteness controls. A stopped producer must
   not manufacture a successful write, callback or completed capture.
4. Verify the new trace survives the real listener/dialer output adapters and
   retained full-log capture; compact omission must not erase the forensic copy.
   No primary verdict/count/finalization change and no report invocation.

Run only affected tests. If any required binding or preservation check is missing,
return no-go before packaging; do not replace it with sampled-event proximity.

## Gate B: separately approve one additional live attempt

Only after Gate A review passes, request explicit approval for the scoped commit,
canonical portable rebuild, strict preflight and exactly one serialized attempt
of `regular-v4-live-activation-off-on-off-256mb`. This is one new allowance beyond
the exhausted6/6 campaign, not a restart or a block of retries. A failed preflight
consumes that attempt. Existing wallet/payment limits must remain unchanged.

Freeze the clean reviewed revision, .NET package, Go sidecar, configuration,
trace settings, baseline manifest and harness hashes. Preserve unrelated changes
outside the frozen build without discarding them; verify their hashes afterward.
Use production Phase5 RouteAcceptance, `StrictCampaignPreflight`, and set both
`GoodputOnlyRerunLimit` and `SetupOnlyRerunLimit` to0. Keep scenario900s,
progress180s and existing report1800s limits; no fault-delay, stop-wait, retry,
chunk-size, queue, wallet or performance-policy changes.

Run GUI/NKN checks outside the restricted sandbox with explicit escalation.
Never bypass a failed preflight. Stop on the first failed gate, hash drift or
newly promoted boundary. No edits/rebuilds during the attempt, no automatic
second attempt, and no standalone retained report beyond normal attempt-owned
finalization. The three standalone report invocations remain consumed.

Select the earliest unresolved batch inside the declared trace window. If the
failure does not recur or lies outside that window, report exactly that; do not
silently widen capture or rerun. Show the last completed stage, first missing or
failed stage, relevant stop event and all completeness limits. Missing records
alone never prove nondelivery. If raw peer receipt exists but application receipt
does not, narrow the next RCA to that boundary rather than blaming Tuna.

Keep the normal operator/GUI verdict and all delivery, route-cycle, integrity and
finalization requirements. Do not convert trace success, safe rejection, SHA OK
or fallback completion into acceptance. No historical verdict is upgraded.

## Current handoff

Gate A is implemented, and both subsequent review findings are corrected and
verified. The application regression now uses real `NknSignalingTransport`
send/receive paths with loopback sidecar clients and a held dispatch barrier;
Go retains its actual terminal reason after an output error, short write or
record-cap exhaustion. See the review-correction receipts in `RESULTS.md`.
The user's `fix issues` approved these scoped corrections. The subsequent
`approved proceed` approved Gate B as bounded above. No second attempt or
standalone evaluator/report is authorized. Attempt receipts are kept separately
under `artifacts/validation/consolidation-bounded-20260907/live-delivery-trace-20260910`.
