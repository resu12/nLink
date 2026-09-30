# File-integrity owner handoff: reviewed contract

2026-09-25, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Follows `file-transfer-terminal-retention-fix-20260925.md`.
Design and source/artifact review only; no code or acceptance change.

## Decision

The next implementation should establish a **private, disposable, continuously
held file-evidence owner in the offline fixture**, before adding a cross-process
file-integrity handoff. Existing terminal captures describe measurement
intervals; they are not a lease that remains valid throughout primary analysis.
Do not turn their exported JSON, matching hashes, or log tokens into a capability.

This is a prerequisite within the existing same-attempt integration contract,
not a replacement for the primary join. Its success must still leave
`FileOwnerHandoffProven=false` and the primary terminal-integrity gate unproven.
No alternative acceptance adoption or live-256 readiness follows.

## Verified current boundary

The last milestone's 395 pinned files still match. Both current-code captures
retain their two genuine terminal rows in the 93-record primary source:

- `artifacts/terminal-source-tokens-20260925/3700ac74-f574-4140-bb5f-2457aa2743ca/`
  (Windows PowerShell 5.1).
- `artifacts/terminal-source-tokens-20260925/e01f6c67-e10b-44ef-8486-7599647ab45c/`
  (PowerShell 7).

Both original observations still report `verified_primary_terminal_source_only`,
`file_owner_handoff_unavailable`, `WriterMembershipVerified=false`,
`GuiCompleted=true`, `GuiIntegrityOk=false`, and primary
`FAIL_PROTOCOL_OR_INTEGRITY` / `fail`. They are independent offline attempts,
not parts of one combined attempt or a canonical Tuna/fallback run.

| Existing boundary | Actual guarantee | Missing guarantee |
| --- | --- | --- |
| `TransferAsync`, `Observe`, `Check` in `FileTransferTerminalIntegrityEvidenceTests.cs:139,219,237` | Real transfer, owned paths/snapshots, protected file measurements and whole-file comparison | `Observe` disposes file handles before returning; `FileDigest` has no native file identity |
| `CheckBinding` in `.SourceBinding.cs:238` | Exact private owner, original log identity/hash, terminal origins and a fresh file check | No continuously held transfer-file lifetime between calls |
| `CheckOriginalPrimaryAsync` in `.Primary.cs:16` | Original log held throughout the child, successful owned child exit, assertions before/after | Only the log is continuously held; primary receives no file-owner capability |
| `LocalGuiWriterRequest.Completion` / `MatchesSource` (`:290,320`) | Original finalized log outputs and retained-source descriptors | None of those outputs is the measured source/destination payload |
| `LocalPrimaryEvaluationInputs.ps1:6` and native primary custody | Original source, arrays, origins, views and invocation ownership | No owner of the transferred files |

This is a missing lifetime guarantee, **not evidence that a file actually changed
during either run**. The existing tests correctly claim point-in-time offline
measurements and are not retroactively failed by this review.

The actual GUI path has the same distinction: `Get-FileSha256Hex`
(`GuiSmoke-Windows.ps1:2545`) closes its stream after hashing. The measured
scenario hashes the source at `:36860`, then destination and size at
`:37643-37655`. Those scalars support today's summary; they do not establish
continuous native file custody or a primary-owned integrity capability.

## Lifetime constraint on the later cross-process design

The current fixture topology is:

```text
C# transfer/file owner (alive)
  -> PowerShell parent -> controlled GUI writer/finalizer -> successful exit
  -> same PowerShell parent accepts writer completion and creates source request
  -> original primary child -> original reports -> observer -> cleanup/exit
  -> C# post-run assertions
```

The controlled GUI worker collects the C# producer's log; it does not own the
C# transfer. `GeneratedSource` is not a file-owner handoff.

`LocalGuiWriterRequest.TakeBodyAfterExit` (`:163`) requires the exact GUI worker's
kernel process handle to signal and exit code to be zero. Keeping that worker
alive until the primary finishes would conflict with the existing protocol.
A future production design must adopt protected file custody into the original
parent **before** producer exit, then bind that adopted owner to the original
source request. It must not remove the successful-exit check or reopen files
later solely by path/hash to bridge the gap.

Existing Frontier handle-adoption code is a lifecycle pattern, not an existing
transfer-integrity receipt. Its file manifest describes the operational source,
drain/completion ranges and diagnostic payloads (`LocalFrontierFileManifest.cs`);
do not reinterpret them as the source and destination of a completed transfer,
increase diagnostic-payload limits for a 256 MiB file, or replace the existing
channel with a generic transport framework.

## Immediate bounded implementation and tests

Limit the next implementation to the terminal fixture partials and one adjacent
test-only helper if needed. Keep the original real 262,144-byte Regular-v4
transfer and both primary-host regressions; no production receipt/codec change.

1. Create the private held-evidence object only from the genuine `BoundOwner`
   and its actual capture after finalization/stream closure. Open source,
   destination and original log read-only with write/delete sharing excluded.
   Read native identities and exact lengths from those same handles; measure
   complete file hashes through them and validate existing snapshots, owner,
   terminal identities/origins and original log descriptors before marking it
   usable. Partial acquisition must dispose every acquired handle.
2. Retain all three handles continuously from this successful measurement until
   the original primary child has exited and the owning test has completed its
   assertions. Do not reopen paths for the closing measurements. This guarantees
   the stated interval only; do not claim it proves that no identical-byte
   replacement occurred before acquisition, or that files cannot change after
   disposal. Longer transfer-time identity continuity is a distinct requirement.
3. Use exact private owner identity, not a copyable record or caller-provided
   `integrityOk` flag, to access the held result. Disposal, failed acquisition or
   loss of any required handle invalidates it; no resurrection or silent reopen.
   Keep diagnostics output-only. This helper is neither a route authority nor
   a production integrity issuer.
4. Exercise a real positive and specific controls: foreign owner with identical
   bytes, relabeled foreign capture, missing destination, changed source,
   truncated/trailing/corrupt destination, invalid terminal capture, acquisition
   failure after a partial open, and use after disposal. Verify real write-open,
   rename and replacement attempts are denied for each held file. After disposal,
   confirm the handles were released but the old held result remains invalid.
   Never mutate an active real run just to simulate a detached predicate.
5. Keep the held owner alive around both existing original-primary child runs.
   Their source-only status and unproven file-owner handoff must remain unchanged;
   post-run JSON checks must not become analyzer input. Verify exact terminal
   rows, original three evaluations, six custody controls, unchanged views and
   unchanged original file identities/bytes through the held handles.

Run new held-owner tests first, then only terminal/source-binding tests whose
shared helper changed and the two primary integrations. Reuse unchanged prior
retention/inference results; do not rerun aggregate lanes, portable builds or
GUI/network scenarios for this test-only prerequisite.

## Required later join (not implemented by that prerequisite)

The subsequent separately scoped native handoff must bind the real producer's
held owner to the actual launching/adopting parent, GUI writer request and
completion, retained-source request and native source descriptor, and original
primary worker identity plus distinct full/compact invocation identities.
Request IDs, process IDs with creation times, tokens and hashes are correlation
fields, not substitutes for the original owned endpoints/objects.

Ownership may transfer across a successful process exit only through an
acknowledged, protected adoption. Before adoption, producer/parent death,
cancellation, closure or timeout fails closed. After adoption, the designated
new owner's lifetime governs the adopted handles; normal producer exit is not
an excuse to invalidate a completed adoption or to accept an unadopted packet.
One-use publication/consumption and cleanup must be explicit. Foreign/replayed
requests, same-byte replacements, copied objects, duplicate consumption, partial
publication and stale/disposed owners require negative controls.

Join at the existing observer seam after original reports and before original
custody cleanup. Use the already computed original objects; no second authority
evaluation or observer-side log replay. Preserve the three evaluations and
4,096-record, 256-boundary/view, 64-operations/boundary, 64-witness and cumulative
20-second diagnostic bounds. Whole-file hashing belongs to its explicitly
bounded producer interval, not an unaccounted primary-side scan.

Writer membership is an additional honest requirement. Today the writer is
opened only for an eligible publication failure witness
(`LocalPrimaryPublicationWitness.ps1:68`). These small successful transfers
have none. Do not invent a failure witness, equate writer-context presence with
membership, or call the one-shot reader a second time. The later implementation
must define an owned membership result in the existing source lifecycle (or
reuse an already established one), with the same budgets. Until then that part
remains unproven even if the file leases are valid.

Leave `nlink-local-publication-forwarding-v2` scoped to publication forwarding,
and leave analysis terminal receipts scoped to analysis completion. A parent-only
comparison of post-run JSON remains a test assertion, not a primary-owned join.
No wire format or public constructor is introduced by this review.

Even a valid future terminal/file join does not supply canonical scenario scope,
the complete Regular4/Tuna4/fallback6/Tuna4/fallback6 cycle, or the closed inventory
of independent failures. Preserve known failed gates, the strict mismatch result,
and the alternative shadow's current state. Never combine the earlier Tuna-pair
fixture with a separate terminal fixture to produce `would_pass`.

## Review verification

Source inspection and verification of the existing current-code artifact pins
only. No new runtime observation or test pass is claimed. All 104 baseline dirty
files and all 395 prior pins remain unchanged; this document is the only new
non-ignored file. `git diff --check` and document whitespace checks pass.
The review seal is
`artifacts/file-owner-handoff-review-20260925/FINAL-VERIFICATION.json`.
No tests, build, live run, full matrix, commit or push were performed.
