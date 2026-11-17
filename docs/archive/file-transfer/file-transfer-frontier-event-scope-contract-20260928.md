# Native frontier event-scope contract review and bounded fix plan

Date: 2026-09-28. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.

Status: **contract review complete; proposed implementation only**.
No production or test source, acceptance rule, native schema, deadline or hold
policy changed. No build/test/transfer, commit or push in this step.

## Decision

Proceed next with a diagnostic-only identity patch under the contract below.
Add two explicit scope-token fields to the reviewed original event emitters and
use them only inside the optional native frontier witness resolver. Retain the
raw fields and their existing redaction. Do not infer scope from writer identity,
timing, route/frontier values, one available receipt or another log row.

A two-comparison-only patch is rejected: the resolver's raw-ID episode partition
would still combine redacted transfers or omit invalidating events.
A global log-string enrichment/redactor hook is also rejected: it would infer
typed identity from text and change unrelated logging.
A new native selected-row-to-receipt schema is unnecessary for this first edge
and remains out of scope.

## Evidence and limits

Latest original run:
`artifacts/native-late-publication-characterization-20260928/pwsh-frontier-late-publication-76715a5ae4d64c8c8becfb6639158c34/`.

Its original resolver reported `native_frontier_receipt_ambiguity`.
Selected semantic line 1056 / physical line 1375 contains two `[redacted]`
scope fields, while the original captured receipt has distinct session and
transfer tokens. There is no exact receipt candidate for that row. The failure
and receipt remain pinned and must not be edited or retroactively tokenized.

The resolver's first comparison and episode partition are at
`tools/FileTransferOps/LocalNativeFrontierWitness.ps1:110-118`.
Required lifecycle lookups span lines 130-224; invalidation/scope checks are at
250-272; pre-progress repair links and opaque episode inclusion are at 284-293.
Later checkpoint/data-session/repair-identity or lifecycle checks are not proven
by this contract. Do not preemptively modify them if this first fix exposes
another rejection.

Entry verification matched 2,429 pins and all 516 existing dirty entries.
The source inventory and saved-input inspection are read-only; no resolver,
receipt producer, native witness selection or authority evaluator was rerun.

## Scope representation

Proposed exact field names:

- `frontier_scope_session_id=fte1-<24 lowercase hex characters>`
- `frontier_scope_transfer_id=fte1-<24 lowercase hex characters>`

Use the existing token domain: SHA-256 of the original ID's UTF-8 bytes,
first 24 lowercase hex characters, prefixed with `fte1-`. No trimming,
case folding, rehashing of an existing token field, or ID substitution.
`CheckpointContinuityEvidenceToken` and
`CoordinatorSnapshotStreamEmitter.EvidenceToken` already use this domain;
the native payload uses it for both source IDs.

One small shared Core logging helper (proposed
`Logging/FileTransferDiagnosticScopeEvidence.cs`) formats the pair for Core,
App and Infra callers. Pass the same typed context, wake, authority, frame,
session or runtime-owner IDs as the existing raw fields, before formatting,
sanitization or redaction. An unavailable/blank/`(none)`/redaction-placeholder
input must not become an apparently usable identity; emit an unusable sentinel
pair, never derive an ID from another object or make logging throw.

Keep helper output diagnostic-only: no caches, mutable state, environment flag,
runtime branch, transport message, persistence protocol or capability.
No coordinator/receipt/publication ownership is created by hashing.
These tokens are pseudonymous correlation values, not anonymization or
cryptographic proof of ownership; original native custody remains mandatory.

The two fields supply the versioned representation through their `fte1-`
domain; no separate profile/authority field is needed.
No secret, payload, wallet value or message text is included.

## Producer inventory and change boundary

The reviewed registry is
`artifacts/frontier-event-scope-contract-20260928/event-inventory.json`.
It has **60 distinct event names**, **88 event/source associations** and
**81 distinct logging locations across eight files**. Dynamic names share a
logging location. The JSON records exact paths, baseline lines and identity
sources; it is not a runtime event catalog or native proof.

| Role | Event types | Consumer obligation |
| --- | ---: | --- |
| Required or competing lifecycle rows | 9 | Exact scope before every unique/prior lookup |
| Other watched frontier/receiver/repair rows | 40 | Preserve episode and scope-change checks |
| Named invalidators | 5 | Never discard a same-transfer invalidation |
| v4 pause/state rows | 4 | Preserve the generic `transfer_paused=1` guard |
| Control-plane delivery observation | 1 | Preserve opaque episode evidence |
| Pre-progress rescue repair link | 1 | Preserve the non-prefix repair-link selection |

Required/competing names: `filetransfer_fallback_frontier_repair_progress_observed`,
`filetransfer_v6_receiver_state_deferred`,
`filetransfer_v6_frontier_recent_progress_grace_deferred`,
`filetransfer_v6_frontier_request_deferred`,
`filetransfer_v6_frontier_stall_grace_recheck_decided`,
`filetransfer_v6_frontier_recent_progress_grace_recheck_decided`,
`filetransfer_v4_sparse_write_committed`,
`filetransfer_v6_sparse_write_committed`, and
`filetransfer_v6_receiver_state_sent`.

Named invalidators: `file_transfer_inbound_terminal`,
`filetransfer_live_route_epoch_terminal`,
`filetransfer_terminalized_by_session_end`,
`filetransfer_data_session_recreated`, and
`filetransfer_data_session_removed`.

The four additional pause names are `filetransfer_v4_pause_state_sent`,
`filetransfer_v4_pause_state_send_failed`,
`filetransfer_v4_peer_state_received`, and `filetransfer_v4_state_sent`.
Do not narrow the resolver's generic pause predicate to these four names.

Important inventory findings:

- Dynamic V6 dispatch names share `PullTransferSessionV6.cs:1834`;
  activation/recovery deferrals share lines 6869 and 6935.
- Infra emits `filetransfer_v6_frontier_request_transport_queued` and
  `..._rejected` at
  `NknSignalingTransport.FileTransferTransportChannel.cs:9532`.
  Both suffixes are fixed by its four reviewed callers.
- Data-session recreation occurs in Core and Infra, removal in Infra, and
  inbound terminal logging in App; restricting the patch to Core loses coverage.
- `FormatFallbackFrontierRepairAuthorityTuple` supplies authority IDs.
  `FormatOutboundSparsePumpInstallTuple` supplies
  `install.RuntimeOwner.SessionId/TransferId`, not an unrelated current context.
- The saved original source has five pre-progress repair-key matches, including
  `filetransfer_v6_post_tuna_fallback_frontier_rescue_requested` at semantic
  line 1011. Its emitter is `FallbackFrontierAuthority.cs:1185`; it does not
  match the obvious frontier-repair prefix. The read-only observation artifact
  records this, without selecting or issuing witnesses.

Add the fields only at the registered logging locations/dynamic emitters.
Do not alter broadly reused tuple formatters merely to propagate fields to
unreviewed events. Shared typed formatting is fine; runtime behavior and existing
raw text/field values remain unchanged.

Four existing evidence paths stay separate and unchanged: coordinator stream
start/end, coordinator snapshots/effects and the native payload marker. Their
original coordinator/boundary/observation/writer/hash/range bindings are not
replaced with these tokens. Do not double-hash their already projected IDs.

## Resolver contract

Parsing is non-mutating: original strings, index events, physical origins,
receipt text, selected failure, candidates and evaluated views stay identical.
A per-row diagnostic scope record may be derived without editing any of them.

1. Preserve existing raw scope duplicate checks. Add strict duplicate detection
   for both new keys on the original line, including case variants and identical
   duplicates. Opaque witness selection must not bypass identity-duplicate
   rejection. Keep unrelated tolerated opaque duplicates (such as the existing
   repeated range fingerprint) unchanged.
2. **Projected mode:** if either new key occurs, require exactly one of each,
   exact lowercase token shape and exactly one existing raw field for each ID.
   An unredacted raw ID must hash to its own projected token. Accept only explicit
   known redaction forms (`[redacted]`; session also `sess_[redacted]`, as in
   the existing publication-use reader). Other partially redacted, absent,
   empty or sentinel raw values do not become wildcards.
   A missing/bad/conflicting projected key cannot fall back to raw mode.
3. **Legacy mode:** only when both projected keys are absent, require two
   nonempty, non-sentinel, unredacted raw IDs and hash each literal using the
   same domain. A token-looking raw ID is still a raw string, not a shortcut.
   Two `[redacted]` values must never identify a transfer.
4. The selected row must resolve one exact pair. Receipt matching still requires
   original writer, both tokens, exact repair-progress association and exactly
   one receipt. Session-only, transfer-only and zero/multiple matches reject.
5. Build the owned episode from exact resolved pairs plus the original writer,
   using the same resolver function for selected, required, competing,
   invalidating and preserved rows. Valid foreign scope is excluded; unresolved
   relevant scope causes omission, not exclusion or success.
6. Determine relevance **before** applying identity filtering. All same-writer
   rows used by the named/family lookups must be scope-resolvable over the
   original lookup ranges (some unique lookups scan the whole source).
   Preserve the existing watchdog interval for terminal/pause/competing-root
   checks. Before-progress rows matching the chosen authority ID and repair
   generation are relevant regardless of event name. Unknown future watched
   names or generic pause/repair-link rows with no usable scope must therefore
   cause conservative omission. Do not shrink these predicates to the registry.
7. Unrelated events do not become proof candidates merely because tokens exist.
   Known coordinator/marker paths retain their separate bindings. All existing
   lifecycle, source-scope, repair, time, route/protocol, exact-order, physical
   origin, writer and snapshot checks still execute after identity association.
8. Stay inside the original one-shot custody callback and work/record budget.
   Continue charging row visits. No new evaluation, second resolver, source
   rewrite, new receipt or witness-range relaxation. Maximum 64 witnesses,
   cleanup and snapshot/report immutability remain mandatory.

Use distinct diagnostic omission reasons for unusable/conflicting/duplicate
scope so another run can identify the first failing condition. They remain
diagnostic reasons; primary failure/verdict and acceptance do not change.

## Redaction and compatibility review

`LocalOperationalLog.Write` redacts before creating the persisted writer batch
(`LocalOperationalLog.cs:71-74`), so tokens must be part of the original
emitter message. Never append them to already sealed historical artifacts.

An `fte1-` token is 29 characters with 24 hex digits: it is below the current
redactor's 40-character general-token and 32-character hex thresholds.
Therefore **no new redactor exemption or production redactor edit is planned**.
This is source inspection, not a claimed test pass. Verify actual redaction,
repeat-redaction, hostile/malformed inputs and raw-ID secrecy with focused
tests. If verification contradicts this assumption, stop for a narrow review;
do not add broad preservation patterns.

Existing legacy fixtures must still match only their actual identities.
Mixed new/legacy rows are allowed only when each independently resolves the same
exact pair. Old fully redacted runs remain unresolvable; no migration/backfill.
Other analyzers and runtime consumers must not begin treating these fields as
authority. Do not change the native producer's own raw-ID validation,
coordinator catalog, manifests, canonical payload or custody schemas.

## Bounded implementation and verification plan

1. Add the small typed pair formatter and registered emitter fields.
   Verify C#/PowerShell/payload token-domain agreement, exact ID sources for
   context/wake/authority/install/frame/session variants, unchanged raw fields,
   and finite inventory coverage including dynamic names. No generic log parser
   or global emitter hook.
2. Add the resolver-local scope reader and replace only the initial ID match
   and raw-ID ownership partition, with pre-filter relevance rejection above.
   Add directly affected positive and negative controls using the original
   native source/receipt/custody path. Synthetic parsed-row fault tests are
   negative-only and must not manufacture a successful native proof.
3. Run the new focused controls first, then the directly affected native join,
   redaction and emitter tests once after stabilization. Reuse
   `FrontierPrimaryJoinTests`/`Support/FrontierPrimaryJoin.ps1` for real
   original-view invariants. No aggregate FileTransferContract/Analyzer/Fake
   lanes merely for this logging/diagnostic change.
4. Review the stable diff and pin new inputs. Only then gate one separately
   authorized rebuilt small offline original-process characterization, with
   strict preflights and no automatic retry. Stop at any new first edge.
   Live-256/full GUI validation is not the next test.

Required controls:

- Legacy unredacted, projected redacted, projected unredacted agreement,
  independently mixed legacy/projected rows and two transfers in one writer.
- Foreign session with same transfer; foreign transfer with same session;
  both foreign; zero/two matching receipts; wrong writer/physical origin.
- Missing one/both fields, sentinels, malformed length/case/domain,
  same-value and conflicting duplicates (including key-case variants),
  raw/token disagreement, partially redacted raw values, no fallback from
  broken projected mode and no literal-redaction hash match.
- Every named invalidator and each pause family remains visible for the owned
  pair; foreign valid scope stays foreign. Unscoped/ambiguous competing,
  terminal, paused, unknown-family and pre-progress repair-link rows omit
  instead of disappearing. Keep original ordering/interval rules.
- Opaque rows reject duplicate scope while retaining unrelated diagnostic
  duplicates; required rows retain all existing stricter duplicate checks.
- Tokens survive real logging/redaction and repeat-redaction, while raw long
  IDs, secrets and unrelated sensitive values remain hidden. No multiline or
  delimiter injection can supply a second accepted scope.
- Witness/source/payload identities, one resolver call, original evaluation
  counts, 64-witness/work limits, ten report hashes, verdict and three false
  authority flags remain unchanged; handles/text are cleared on rejection.

A successful scope patch does not establish route correctness, a recovered
failure, timely publication or acceptance. A later rejected proof edge is new
evidence to diagnose, not permission to relax another rule.

## Preservation and handoff

Only this contract document is a new non-ignored file (517 dirty entries).
The inventory, entry audit, source observations and final preservation seal are
under `artifacts/frontier-event-scope-contract-20260928/`. All existing 2,429
pins and 516 dirty file contents/statuses remain unchanged.

Next action: implement this bounded diagnostic scope contract and its focused
controls when directed. The current review does not itself implement or execute
that phase.
