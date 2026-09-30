# Owned terminal and payload join

2026-09-25, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Implements the bounded offline milestone in
`file-transfer-primary-integrity-consumption-review-20260925.md`.
Evidence finalization resumed on 2026-09-26 after interruption; no code or tests
were changed or rerun during that recovery.

## Result and scope

The original primary can now join its owned writer membership/origins to the
genuine producer's validated transfer binding and continuously held payload
measurements. Both PowerShell hosts pass. This is an offline, single-writer,
256 KiB Regular-v4 fixture result, not a production integrity-gate cutover.

The original GUI integrity result remains false, the primary verdict remains
`FAIL_PROTOCOL_OR_INTEGRITY`, and the independent live-route proof remains
failed. Acceptance, runtime/coordinator code, alternative acceptance, route
requirements and original reports are unchanged.

## Implemented connection

The existing fixed native packet is now version 2. The genuine
`HeldTerminalEvidence.SendToNativeParent` still performs its original
`CheckCurrent` validation before issuing any packet. After that validation,
it adds the actual session/transfer evidence tokens and the two
direction-specific producer terminal-line positions. No caller-supplied
capture, hash receipt or file path is accepted as a new issuer.

The native parent validates token format, distinct positive positions and
bounds, retains the binding inside its original file owner, and forwards it
unchanged through the already-bound request to the primary. Both hops retain
the existing native handles, process/request checks and acknowledged
adoption-before-release ordering. Version 1 and missing/partial bindings are
not interpreted as version 2. File descriptors remain the sole transported
payload-size/hash measurements; no extra file read or hash pass was added.

The guarded `TerminalPayloadJoin.cs` partial requires the exact original
worker, native file owner, source reader, evaluation custody/pair, context,
arrays, views, running cumulative clock and successful membership/origin join.
It uses the membership owner's private origin map to locate the two expected
producer lines. Candidate indexed terminal records must reference those
original source strings and exact semantic line numbers. Only then are the
two original strings checked for unique event/direction/session/transfer,
completion/error and exact byte-count fields. Opposite directions and equal
protected source/destination measurements are required.

A foreign/copy/stale owner cannot consume the eligible attempt. A valid-owner
attempt is one-shot; data/budget failure cannot be repaired and retried to
issue a result. Cleanup or expired custody makes the result unusable.
Historical issuance is a separate output-only diagnostic from current access.
No publication failure witness, analysis completion or route authority is
issued.

The optional probe exposes `TerminalPayloadJoin` and sets
`PrimaryFileIntegrityJoinProven` only when this owned offline join succeeds.
Its diagnostic missing edge becomes
`primary_integrity_gate_not_integrated`; a failed terminal binding reports
`terminal_payload_binding_unproven`, and a failed provenance join retains
`file_writer_provenance_unproven`. These are probe diagnostics, not native
analyzer verdict classes. The original `TerminalIntegrityGate=unproven`
continues to describe the unintegrated acceptance gate.

## Work and lifetime bounds

The native packet stays at 16 KiB maximum, the original log at 4 MiB maximum,
and each payload at exactly 262,144 bytes. No bound was increased.

The additional traversal costs at most `full.Length + 2` record visits:
one integer/reference pass over original source positions and two terminal
checks. The preflight adds those visits to the existing
`7 * full + 4 * compact + membership visits + origin-join visits` total and
rejects totals above 4,096. The two bounded field parses charge four times
their string lengths against the existing cumulative producer code-unit
budget, enforce the original line-size bound and at most 64 field segments.
They do not replay the complete log or touch payload streams.

The original 20-second cumulative clock is reused and checked through
consumption; the diagnostic work time now includes this new work. The
256-boundary/view, 64-operations/boundary and 64-witness bounds are unchanged.

Final positives:

| Fixture | Source rows | Added visits | Total visits | Added code units | Cumulative work |
| --- | ---: | ---: | ---: | ---: | ---: |
| `30b6bf15-a85f-44fb-a76f-e5fa76892581` | 93 | 95 | 1,266 | 7,488 | 279 ms |
| `fab8d16c-11a3-4219-9717-f2f0969e03c6` | 93 | 95 | 1,266 | 7,488 | 308 ms |

Both retain three original authority evaluations, unchanged input views,
zero additional writer reads, no primary payload hashing, no selected failure
witness and no analysis completion. Both report historical issuance and
retirement in original cleanup.

## Focused verification

Results are under `artifacts/terminal-payload-join-20260925/results/`.

- `payload-join-focused.trx`: 9 passed, 2 failed. All rejection cases passed;
  both positive joins succeeded, but their cleanup-history assertion failed.
- `payload-join-cleanup.trx`: 3 passed: both corrected positive cleanup cases
  and the corrected detached-direction control.
- `payload-join-final-timing.trx`: both positive hosts passed after the final
  private issuance accessor and inclusive work-time reporting adjustments.
- `payload-join-regression.trx`: all 14 directly affected native-parent,
  native-primary, membership and non-membership collector-primary tests passed.
  This regression lane ran once.

There are **25 distinct passing tests** in the final evidence. Across all
30 attempts, 28 passed and two failed; the failed development artifacts remain
recorded and are not reclassified.

The two initial positive failures were diagnostic-only: cleanup tried to read
a currently usable membership after primary custody had already ended. The
fix keeps a private historical issuance bit accessible through an output-only
query, while current access still fails after cleanup. The initial detached
direction control could reconstruct the original text through two opposing
replacements; it was changed to one actual direction swap and rerun.

Twelve ownership substitutions are rejected before new record work. New
negative cases cover a wrong transfer token, swapped direction coordinates,
an absent producer coordinate, duplicate terminal, changed detached direction,
record-budget exhaustion, expired original clock, unsuccessful origin join,
and a genuine second same-byte transfer. In the latter, two actual transfers
share the frozen writer; the second pair cannot satisfy the first producer's
binding. It does not claim independent-peer NKN evidence.

The native-parent control case now exercises 11 rejected packets, including
old version, missing/malformed/truncated transfer binding, duplicate coordinates
and coordinate overflow, alongside the existing request/process/scope/file
and truncated-frame controls. Native-primary request/descriptor/partial-packet
rejections and original non-join paths remain passing.

Token/coordinate/budget/clock mutations inside the helper are deliberate
in-memory fault injections, not hostile native transport histories or claims
that reflection is a security boundary. The separate malformed-packet controls
use real child stdin. No process-death injection during the new join, multi-writer
join, 256 MiB payload, GUI or live-network run was performed.

## Build, preservation and next boundary

The first sandboxed build stopped because Avalonia's build-telemetry task
attempted to write outside the workspace. The build succeeded with the
per-invocation `-p:UsedAvaloniaProducts=` switch, using the existing local package
target's opt-out condition. No project setting or runtime behavior was changed.
The successful build reported 257 warnings and no errors. Guarded PowerShell
support compiles afresh in each isolated host.

All 9,547 previous pins matched before implementation. Eleven previously dirty
test/support files changed; the other 148 baseline dirty files are preserved.
Three test/support files and this report are new. The rebuilt smoke-test DLL
is the only additional changed prior pin. Previous evidence files are unchanged.
PowerShell parsing and `git diff --check` pass. The final record is
`artifacts/terminal-payload-join-20260925/FINAL-VERIFICATION.json`.

Next: review pre-report integrity consumption and the complete independent
failure inventory before any production gate integration. The real GUI still
needs equivalent producer ownership and declared scenario/size scope.
The full Regular4/Tuna4/fallback6/Tuna4/fallback6 cycle remains required.
A valid payload join cannot erase the current failed live-route proof.

No production acceptance change, portable rebuild, aggregate suite, live-256,
full GUI matrix, commit or push was performed.
