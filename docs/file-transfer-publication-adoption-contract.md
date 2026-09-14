# C1o: private post-terminal-receipt adoption

2026-09-14, base HEAD `ef78e9d7`, with accumulated uncommitted work.
This is an opt-in retained-parent diagnostic path. Upstream live callers and the
GUI-owner relay remain disabled; no new live 256 MiB test is part of this slice.

## Original parent and terminal-write custody

Both actual retained parents accept an optional empty `LocalPublication` reference.
It requests adoption only alongside the existing source-ownership flag and genuine
accepted GUI writer context. Missing prerequisites preserve ordinary source-only
or writer-bound analysis and omit publication. A prefilled output is never replaced.
No environment variable or live callsite enables the feature.

The original source hash pass creates C1n's parent-bound request. The parent claims
one private `ParentAdoptionCustody` before launching/binding its worker, pinning the
actual artifact directory, start timestamp, compact mode and route-proof mode.
The request already pins owner, attempt/run/scenario and the source hash/count from
the verified lease. The capability cannot be created after worker binding, copied
from a receipt, or obtained through a public constructor/getter.

The retained parent keeps the request, custody, output reference, GUI owner and
terminal return private. The original receipt writer receives custody through a
private parameter. Neither the atomic JSON writer nor optional formatter receives
that capability. The receipt writer accepts only the genuine loaded custody type;
caller-authored lookalikes are ignored without invoking their methods. This is
trusted parent/callsite custody, not an authentication
API for arbitrary code already holding the private capability. Same-account
malicious code or explicit private reflection is outside the isolation premise.

The original process wait, stdout/stderr persistence, diagnostics, proof-artifact
checks and receipt construction run unchanged. Immediately before the existing
atomic receipt write, custody snapshots the bounded original receipt dictionary.
It requires the exact successfully exited worker handle, original request/source
facts, unchanged final input hash/count, compact authoritative SnapshotV1 metadata,
passing full/reduced parity and all eleven original artifact path/hash bindings.
No extra input or artifact read/hash is used to obtain these facts.

The fixed status is `completed` when the original proof gate passes, otherwise
`proof_incomplete`. A failed proof may carry diagnostics; missing artifacts,
nonzero/unknown/running exit, timeout or changed input may not. The original
passing-proof validator is not weakened or reused for this optional path.

Only after the atomic write returns successfully does the original receipt writer
record its exact returned object. It rechecks the captured dictionary for mutation.
If writing throws, recording fails or the dictionary changes, the channel is
revoked and the original exception/result is preserved. The returned mutable
object is an identity anchor only: copied/deserialized returns are rejected, and
its fields cannot nominate trusted status, hashes, paths or process identity.

## One-shot adoption and catalog binding

After the original terminal receipt write, the actual parent passes that exact
return to custody. Custody checks owner/lifetime and captured dictionary again,
then performs C1n's one-shot bounded channel read. It compares an independent
catalog digest from the original receipt's Snapshot metadata with the digest
issued from the actual primary catalog component.

The completion record now carries `CatalogSha256`. It hashes the same canonical
four-field catalog object already contained in the V2 payload. C1m computes it
before report publication under its existing budget; payload bytes/formats do not
change. The channel stores the digest as 32 bytes at offset 332, still inside its
384-byte control mapping. Its magic advances to `0x4e504332` so an older control
record cannot masquerade as a catalog-bound completion. C0 V1 and C1a are unchanged.

The result is a distinct immutable `AdoptedPublication` with a privately issued
terminal receipt and C1n completion. It has no public constructor/setters and
exposes only copied payload bytes. The terminal status is retained, never upgraded
to a passing verdict. Adoption occurs before the parents' existing nonzero/proof-
failure throws, so a `proof_incomplete` diagnostic result and the original failure
can coexist without reclassification. Receipt bytes are not rewritten.

An early, copied, expired or repeated take consumes/omits the optional attempt.
It cannot retry, replace a result or revive the worker. Parent disposal clears
receipt references and freezes the channel. Already returned immutable diagnostics
retain their historical receipt metadata, not ongoing route/runtime authority.

## Bounds and verification scope

Receipt custody admits at most 64 top-level entries and eleven two-field artifact
dictionaries. It copies only strings, booleans and 32/64-bit integers. Strings are
limited to 4,096 UTF-16 code units each and 32,768 accounted string/key units total;
keys are separately bounded to 128 units. Genuine PowerShell `PSObject` scalar
wrappers are unwrapped through the runtime type, without invoking user-authored
properties or conversions. Unsupported values omit the whole adoption.

The active metadata/check/channel-read budget is cooperatively capped at 250 ms,
paused across the original atomic write. Catalog canonicalization uses the fixed
codec already loaded/used before launch, over four bounded scalar values, not a
new general serializer or private-view graph. The channel still caps payloads at
256 KiB and uses only zero-wait mutex acquisition. No watchdog, grace period,
network/file read, additional authority evaluation or whole-input traversal is
added. The parent source hash/line-count operations remain the original two each.

Generated tests use actual parent functions, original receipt writer, actual GUI/
retained ownership types and the real two original generated evaluations. Primary
report files are explicitly controlled lifecycle fixtures with the original
generated catalog identity; they are not live transfer evidence. Separate existing
regressions check real report behavior and unmodified receipt/parent paths.
The matrix has nineteen cases per parent on both Windows PowerShell 5.1 and
PowerShell 7. Each parent has its own test entrypoint so it stays within the
existing five-minute per-host harness limit; neither that limit nor the original
1,800-second production watchdog is extended.

Verification passed fifteen unique xUnit tests: the two new parent tests (76
cases total) and thirteen directly affected regressions, including the prior
54-worker completion and 46-worker channel matrices. Results:
`artifacts/publication-adoption-20260914/RESULTS.md`.

## Remaining upstream boundary

The boundary below records C1o's stopping point. Follow-up
[C1p](file-transfer-local-gui-writer-relay-contract.md) now supplies the opt-in
one-hop GUI-owner relay for the normal RouteAcceptance workflow. Direct
Wrapper-owned analysis and top-level live callers remain disabled. C1p's next
gate is the combined offline integration with real retained primary reports.

The main Wrapper call to `Invoke-TunaGuiSmokeWithHardDeadline` still does not
request `LocalWriterCompletion`; its retained-analysis calls do not carry that
context or request publication. RouteAcceptance invokes the Wrapper through
`Invoke-RouteAcceptanceChildScriptNoThrow`, which currently returns an exit code
from a native PowerShell invocation, not an independently held child handle or
an adopted typed GUI context. Those are the next missing ownership edges.

Any opt-in relay must bind the actual Wrapper child before accepting its result,
carry only the privately accepted GUI completion under exact run/scenario/root
scope, and preserve original output/exit and retained-analysis ordering. Saved
manifest/ledger JSON, matching hashes, or a zero exit code alone cannot construct
that context. Missing, failed, stale or copied relay state must omit diagnostics
without changing primary behavior. Milestone-merged, secondary-transfer and
synthetically augmented sources remain ineligible unless the existing exact
source association genuinely succeeds; do not alter inputs to force eligibility.
