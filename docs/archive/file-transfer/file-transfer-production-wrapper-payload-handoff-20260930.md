# Production wrapper payload listener and completion

The production wrapper now starts the original payload listener, checks the
received pair after normal GUI exit and output drain, and releases its custody
during cleanup. Optional payload rejection preserves the original completion
without a second take. Eighteen focused offline cases passed. Parent payload
transfer and primary integration remain separate, uncompleted production steps; this is
not live route-cycle acceptance.

No application routing, acceptance rule, goodput policy, timeout, portable
package, live preflight, GUI/NKN run, commit, or push changed in this phase.

## Starting state and scope

The predecessor is
`artifacts/production-gui-closed-payload-handoff-20260930/FINAL-VERIFICATION.json`,
SHA256 `690825b0545cadff5066e512d19f891b2318d3efdbdb8e4765fe6a7394470771`.
All 686 dirty entries matched before editing. HEAD remains
`a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`, with no staged changes.
Before-images are retained under this phase's artifact directory.

Only two production tooling files changed:
`Run-FileTransferTunaGuiSmoke.ps1` and
`LocalGuiWriterCanonicalPayloadWriters.cs`. Two existing fixtures were adapted,
and three focused test files were added. The saved payload-wrapper probe was
not changed.

## Preserving completion while keeping payload checks strict

The wrapper retains the listener privately from its original GUI reports. Its
existing exit, output-drain, and state-persistence gates still precede the
terminal take. The existing payload APIs enforce successful GUI exit, original
reports, committed native handles, file identities and hashes, and exact closed
writer association. Their work budgets and one-use checks are unchanged.

Inspection identified a composition pitfall: `TryTakeWithWriters` consumes the
original completion before validating payload writer association. Returning
only its strict result would erase an otherwise valid ordinary completion on
payload rejection; retrying the take would also be wrong.

`TakeAfterExitWithPayloads` therefore retains the original result and whether
the take was attempted. The existing strict API delegates to the same core and
still returns null for an invalid association. The new wrapper-facing method
returns the original completion even if the optional association fails after
the take. If no take occurred, it performs the original take once. Copied or
already-consumed requests cannot take again. A non-null completion is not proof
that payload writers were bound; that remains `NativeWritersBound` on the
original payload wrapper.

The wrapper disposes the pair before disposing parent custody and its request.
It does not yet offer the pair to the parent. Existing ordinary completion
forwarding and failure handling remain in place.

## Offline verification and limits

The initial probe failed with `Production wrapper payload callers missing`.
The new normal-loader tests execute the production listener, terminal gate,
and cleanup blocks with controlled startup failure, nonzero exit, unavailable
exit status, incomplete output, absent request, and success cases on both
PowerShell hosts.

Six integration cases execute the production wrapper listener and completion
calls with the preceding production GUI offer block. They cover valid and
foreign-writer pairs on both hosts, plus hash mismatch and missing optional
custody on PowerShell 7. They assert original completion identity, rejection of
copied/repeated requests, unchanged authority flags, and release of both
256 MiB files. All six passed without a production repair after the first run.

The pair fixture still uses test-augmented native loading and headless role
processes. It does not prove a full normal-loader GUI transfer or a live Tuna
route. The old component tests retain their explicit listener/take probes;
the new caller tests do not substitute those production calls.

Four existing strict valid/foreign-writer cases and two ordinary wrapper cases
also passed. The ordinary wrapper cases use the normal loader without a
canonical payload pair and preserve the later primary-source handoff.

Final fixture inspection also found a terminal-collector probe matching the
old completion-call text. Its single replacement target was updated, with no
change to the collector operation. All four collector regressions passed:
successful exit on both hosts, nonzero exit, and failed output capture. The
preservation audit was stopped before sealing and restarted after this fix.

The build passed with 339 warnings and zero errors. It used escalation for the
existing Avalonia build-service filesystem requirement. Tests stayed offline
in the normal sandbox; no aggregate suite or full matrix was run.

## Next parent payload handoff

Connect the original parent payload listener and its nonblocking pull, the
wrapper's `TryOfferPayloadPair` before native writer forwarding, and the
parent's exact terminal take. Inspection found no production PowerShell calls
to `TryStartPayloadParent` or `TryOfferPayloadPair`; those remain in fixtures.
Preserve ordinary completion on optional rejection and keep payload ownership
separate from route authority.

Validate those actual parent callers offline, then review primary integration.
A reviewed checkpoint, fresh portable build, strict escalated preflights, and
targeted live validation still follow. Acceptance continues to require the
second coordinator-published `file_tuna_v4` route.

Evidence and the immutable final seal are under
`artifacts/production-wrapper-payload-handoff-20260930`. The verifier requires
eighteen distinct latest passing cases, including strict writer, ordinary
wrapper, and terminal collector compatibility, preserved predecessor pins and
dirty work, terminated recorded children, released payload files, valid PowerShell syntax, and a clean
whitespace diff before writing the seal.
