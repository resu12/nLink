# Production native picker and runtime reads — 2026-09-29

## First missing connection

The preceding checkpoint delivered a protected native file to the original
helpee, but only the test host consumed it. The real picker still returned a
path-opening factory. Native opt-in could therefore silently use ordinary file
selection without native lifetime custody. A focused regression reproduced that
behavior before implementation: an exception was expected, but none was thrown.

All 11,790 predecessor pins were verified before changes. Baseline:
`production-canonical-sender-20260929/FINAL-VERIFICATION.json`, SHA256
`0cb4128ae6544e6c3403fcd9ff27f6274a39a9819874c1a1ff90ebc988938a45`.
HEAD remains `a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`.
The baseline had 651 dirty entries; before-images preserve ten edited text
files and five pinned build outputs. Unrelated changes are not in the allowlist.

## Implemented contract

- Only the original canonical helpee launch gets the native-selection opt-in.
  That environment marker requests strict handling; it grants no ownership.
  The original app lifetime, app lease, packet, and held-file identity are still
  required. Copies and repeated selection are rejected.
- The original GUI completes its bounded packet write before invoking the
  picker. `ReceiveAvailable` peeks at the native stdin pipe and requires the
  entire length-prefixed packet, capped at 512 body bytes, before reading it.
  Empty and partial open pipes are rejected immediately, without a waiting
  worker, timeout extension, or stdin polling. Cancellation is checked before
  consumption and after receipt; cancellation after receipt closes custody.
- The actual picker returns the native selection's descriptor and factory.
  Missing lifetime, packet, or source path cannot fall back to ordinary path
  opening or the interactive picker. Unmarked ordinary selection and the
  existing successful autopick telemetry retain their behavior.
- The native selection belongs to one original app-owned service, descriptor,
  and outbound context. Foreign services, descriptor copies, selection copies,
  and repeated send binding cannot consume the original selection.
- Metadata may open once, at `PreparingMetadata`, on the original Regular V4
  route. Each payload view requires a fresh current sparse-pump installation,
  the matching data session, and current coordinator-issued runtime-owner and
  route-publication tokens. The factory does not issue those tokens or change
  route state. Valid routes remain Regular V4, Tuna V4, and fallback V6.
- Successor reads reopen the retained native file, not a path. Native reopen
  occurs outside the service gate; the owner/install tuple is checked again
  before retaining the view. A rejected open does not retire the active view.
  A successfully admitted successor retires its predecessor. There is no
  fixture-specific four-read cap: later coordinator-authorized installations
  still require a strictly newer install generation and the same custody.
- Read and seek operations reject retired/copy views. Read buffers are cleared
  if an operation fails after beginning, including failed post-read ownership
  checks. App shutdown disposes the native selection and anchor; transfer
  retirement alone does not turn the anchor into a reusable selection.

No route authority, route acceptance, retry policy, throughput setting,
diagnostic limit, or live-test budget was changed.

## Verification

The initial native-opt-in regression failed as expected. Two implementation
compile errors (nested-member accessibility and static snapshot access), then
one existing carrier-fixture entry-point reference, were corrected. The final
test-project build passes with 356 warnings and zero errors; the new native
selection implementation has no warnings in that build.

All nine focused tests passed (3m03s), including original metadata selection on
both PowerShell hosts; wrong-request, missing-packet, partial-packet and canceled
selection; and native Regular → Tuna → fallback reads with valid and disposed
custody. Empty/partial pipes remain open during the rejection test, which checks
that the picker returns within two seconds rather than waiting for EOF.

The metadata cases use the actual picker callback and automatic service binding.
The route fixture wraps streams only to schedule its existing offline transport
transitions, so it binds the production selection to the already-created real
context explicitly. It does not synthesize coordinator tokens, routes, or pump
installations. These cases check copied readers, duplicate opens, retired views,
source SHA, fallback bytes, and original-anchor lifetime. They do not prove a
complete live route cycle or exercise a second Tuna activation.

The affected compatibility lane passed all 13 cases (3m14s). It includes the
finalized picker guards, native metadata/runtime success, four existing
retained-runtime cases, two small real-Core/fake-transport correlation cases,
ordinary autopick, and normal/early-exit app lifetime. Four cases overlap the
focused run: the total is **18 distinct green tests**, with no remaining
failures or skips. No aggregate or full GUI suite was run.

Three new files and one newly modified picker file bring the dirty count to
655. The phase verifier checks prior content/status preservation, before-images,
PowerShell syntax, whitespace, the unchanged live entry, and recorded process
cleanup. It records the final results and source/artifact pins in a new seal.

## Next gate

The top-level live call is still
`Run-ScenarioFileTransferTunaHandoffFallback -Context $ctx`, with no canonical
owner/attempt enabled. Native app selection and runtime reads are now connected,
but destination adoption and the remaining writer/payload handoff must be joined
to that same original attempt before enabling live execution. Then a reviewed
checkpoint, fresh portable package, and strict preflight are required. No live
NKN, live-256, full GUI matrix, portable rebuild, commit, or push was performed.
