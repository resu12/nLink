# Native frontier data-session identity contract and bounded fix plan

Date: 2026-09-28. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.

Status: **review complete; implementation proposed, not performed**.
No production/test source, acceptance rule, native schema, timing or runtime
authority changed. No build, test, resolver replay, transfer, commit or push.

## Decision and evidence

Add one diagnostic projection of the original typed data-session identity at
five existing emitters. Resolve it consistently for the optional native
resolver's wake checks and watchdog scope-change checks. Keep generation checks
separate and retain the existing writer/session/transfer ownership partition.

The latest original run remains:
`artifacts/native-late-publication-characterization-20260928/pwsh-frontier-late-publication-e315d617c7884323b805239e5b8987e4/`.
Its original optional resolver ran once and omitted with
`native_frontier_data_session`. The primary remains `FAIL_PROTOCOL_OR_INTEGRITY`,
gate `fail`; zero witnesses and no optional output were committed. Transfer
integrity succeeded for the 172,032-byte offline named-pipe carrier, not a live
NKN/GUI-256 route test. This review does not change those outcomes.

The selected failure is semantic 985 / physical 1292. Its writer and projected
session/transfer pair match the original receipt. The first relevant recent arm
is semantic 947 / physical 1244, frontier 1, wake 1, data-session generation 1;
its raw identity is `data_session_identity=[redacted]:1`.

At `tools/FileTransferOps/LocalNativeFrontierWitness.ps1:88`, the resolver hashes
that logged raw text. At
`src/nLink.Core/FileTransfer/SessionFileTransferService.FrontierPublicationPayload.cs:88`,
the producer hashes the original typed `wake.DataSessionIdentity`. This is the
first observed representation mismatch. The saved observation does not export
the receipt's data-session token; this review does not recover or reconstruct it
from transfer ID, generation, coordinator snapshots, timing or neighboring rows.
Later proof checks are unvalidated.

The read-only inventory is
`artifacts/frontier-data-session-scope-contract-20260928/source-inventory.json`.
All 15 watched rows carrying this exact raw field in the saved 1,247-row source
have `[redacted]:1`, generation 1, and the original receiver's writer/pair. They
cover the five emitter names below. Parsing saved fields is not another native
selection, resolver call or authority evaluation.

## Exact producer boundary

All five sites are in
`src/nLink.Core/FileTransfer/SessionFileTransferService.PullTransferSessionV6.cs`.
Lines below refer to the pinned pre-implementation source.

| Line / event | Original typed source | Consumer |
| --- | --- | --- |
| 6024 `filetransfer_v6_fallback_frontier_repair_progress_state_forced` | `progressReceipt.DataSessionIdentity` | Conditional watchdog scope; preserved episode evidence |
| 8468 `filetransfer_v6_frontier_request_deferred` | Existing `CreateReceiveRecoveryDataSessionIdentity(context.TransferId, context.DataSession, context.DataSessionGeneration)` result | Required stall wake and competing-root/watchdog scope |
| 8477 `filetransfer_v6_frontier_stall_grace_recheck_decided` | `wake.DataSessionIdentity` | Required successor wake and watchdog scope |
| 8485 `filetransfer_v6_frontier_recent_progress_grace_deferred` | `wake.DataSessionIdentity` | Required recent-arm wake and watchdog scope |
| 8493 `filetransfer_v6_frontier_recent_progress_grace_recheck_decided` | `wake.DataSessionIdentity` | Required recheck wake and watchdog scope |

Pass the exact value already used by the raw field, before trimming/redaction.
At line 8468, evaluate the existing factory once into a local and reuse it for
both fields. Do not replace current-context identity with the captured wake's
identity: that could conceal a genuine change. The other three wake sites must
not switch to a current context. The forced-state site keeps its validated
progress receipt, not an inferred context or authority tuple identity.

The factory (`OutboundTransferController.cs:1745`) returns `(none)` for a null
session, otherwise the original transfer ID plus generation. That explains the
observed redaction shape; it is **not permission for the analyzer to reconstruct
the identity**. The producer must project the already obtained typed value.

No global log enrichment, shared authority-tuple modification or redactor edit.
No additions to the other 76 prior session/transfer logging sites. Other
data-session fields, including prefixed source/receiving/progress fields,
coordinator snapshots, route-activation logs and transport observations are not
this contract's raw `data_session_identity` field.

The five named terminal/recreation/removal invalidators and generic
`transfer_paused=1` already reject an owned watchdog episode independently of
data-session identity. Keep them unconditional; do not add a data-session filter
or require these invalidators to match the old session before rejecting.

## Representation and redaction

Proposed field: `frontier_scope_data_session_identity=fte1-<24 lowercase hex>`.

Use the existing native evidence domain: SHA-256 of the original identity's UTF-8
bytes, first 24 lowercase hex characters, prefixed with `fte1-`. No trimming,
case folding, token substitution or concatenation of other projected fields.
A token-looking raw identity is still a literal input and is hashed normally.

Add only a small `FormatDataSession(string? dataSessionIdentity)` method to
`src/nLink.Core/Logging/FileTransferDiagnosticScopeEvidence.cs`, reusing its
existing stateless token primitive. Keep the existing two-ID formatter unchanged.
Null, whitespace, `(none)` or any `[redacted]`-containing input emits an unusable
`(none)` projection, not a plausible token. Never make logging throw for such
inputs. No cache, switch, capability, runtime branch or transport message.

Keep raw fields and generation values unchanged. Tokens are pseudonymous
diagnostic correlation values, not proof of custody or anonymization. They do
not authorize route publication, retries, fallback, progress or recovery.

`LocalOperationalLog.cs:71-74` redacts before creating persisted batches. The
projection must be in the original emitter message, never appended to saved
logs or receipts. The 29-character token is below the current redactor's
32-hex/40-general-token thresholds. Verify this through focused real-redactor
tests; if it fails, stop for a narrow review rather than adding exemptions.

## Resolver contract

Add a resolver-local, non-mutating data-session reader over the original line
and parsed fields, yielding a token/presence/error record. It may be stored next
to the existing per-row session/transfer record. Do not rewrite fields, strings,
index events, receipts, failure objects, views or physical origins.

### Parsing rules

1. Count exact raw `data_session_identity` and new
   `frontier_scope_data_session_identity` keys on the original line, including
   key-case variants. Identical or conflicting duplicates reject wherever this
   data-session evidence is consumed. Do not rely on the parser's first-value
   behavior. CR/LF-containing inputs cannot resolve.
2. If the new key occurs, require exactly one exact lowercase
   `fte1-[a-f0-9]{24}` value and exactly one nonempty, non-sentinel raw field.
   Malformed/missing/conflicting projected evidence cannot fall back to legacy
   mode. An unredacted raw value must hash to that projection.
3. The only permitted redacted raw shapes in projected mode are `[redacted]`
   and `[redacted]:<one or more ASCII decimal digits>` (anchored whole-value
   match). The latter is the observed producer/redactor shape. Reject prefixes,
   suffixes, multiple placeholders, signed/nondecimal suffixes and other partial
   redactions. The suffix is never used to derive a token, establish generation
   or bypass the separate generation checks.
4. Only when the new key is absent may a single nonempty, non-sentinel,
   unredacted raw value resolve by literal hashing. Redacted legacy text remains
   unavailable even if its literal hash could match supplied evidence. Mixed
   legacy/projected rows are compatible only if each resolves independently to
   the same exact token. Old redacted artifacts are not backfilled.
5. Use separate diagnostic reasons for unavailable, invalid, duplicate and
   conflicting representation. Retain `native_frontier_data_session` for a
   valid normalized token that differs from the original receipt. These reasons
   do not alter the primary verdict or acceptance.

### Consumption and relevance

- The four `Scope -Wake` call sites (current resolver lines 200, 207, 219, 226)
  require a resolved token equal to the original receipt's
  `Source.DataSessionIdentity`. Preserve every existing route/runtime/leg/
  checkpoint/publication/generation check, including `DataSessionGeneration`.
  Pass/access the original row rather than reconstructing a line from fields.
- In the existing watchdog interval, preserve the same owned writer/pair and
  watched-family predicate. Replace only the raw data-session equality with
  normalized equality against the selected stall's token. Keep the separate
  raw `data_session_generation` comparison and all other scope comparisons.
- For the five registered names inside that interval, identity is required;
  deleting both raw and projected fields must not evade checking. For other
  watched names, retain conditional behavior: if either identity key exists,
  it must resolve and match; with neither key, do not invent a new requirement.
  A valid changed token with unchanged generation must still reject with the
  existing watchdog scope-change outcome. Unknown future names carrying these
  keys must not bypass the watched-family check.
- Data-session identity must **not** enter the ownership partition, receipt
  candidate selection or relevance filtering. A changed/unavailable data session
  on the same writer/session/transfer is evidence to reject/omit, not a foreign
  event to discard. Existing valid foreign writer/pair behavior is unchanged.
- For regular diagnostic rows included through the existing opaque episode or
  pre-progress repair-link path, reject duplicate raw/new data-session keys.
  If a new projection is present there, require its valid raw/projection binding
  even when not used for a wake comparison. Legacy raw-only opaque fields not
  otherwise consumed remain uninterpreted; do not turn them into ownership or
  scope proof. Preserve unrelated tolerated duplicates such as range fingerprints.
- Do not enforce data-session parsing errors globally. The saved source contains
  an unrelated sender row, semantic 140 / physical 249,
  `runtime_unlock_initial_activation_receive_recovery_hold_decided`, with raw
  values `[redacted]:1` and `(none)`. It is outside this receiver's watched proof
  and must remain outside this change. A global duplicate check would introduce
  a new, unrelated failure. Coordinator stream/snapshot/effect and marker paths
  retain their separate bindings and opaque policy; do not rehash their already
  projected raw data-session values through this new reader.

Stay inside the original one-shot custody callback and existing input/visit/
witness budgets. No second resolver/evaluator, receipt reconstruction, source
rewrite, broadened time interval or relaxed ordering. Maximum 64 witnesses,
original snapshot/report immutability and cleanup remain mandatory.

## Bounded implementation and verification

1. Add the formatter and five emitter insertions only. Update focused emitter
   coverage with exact original typed sources and unchanged raw/generation
   fields. No native payload, boundary validation, coordinator catalog, schema,
   route invariant, timing profile or redactor change.
2. Add the local reader and the two consumer integrations above, including
   scoped opaque duplicate handling. Leave producer raw-ID validation at
   `FrontierPublicationBoundaryValidation.cs:390` unchanged. Do not preemptively
   change checkpoint, repair or later lifecycle comparisons.
3. Add and run directly affected formatter/redactor/parser controls, then the
   directly affected original native-join controls once the patch stabilizes.
   Reuse `FileTransferDiagnosticScopeEvidenceTests`, `FrontierEventScopeTests`
   and `FrontierPrimaryJoinTests`/its support path. Exercise both `pwsh` and
   Windows PowerShell where the existing parser lane does. No repeated aggregate
   FileTransferContract/Analyzer/Fake suites or unrelated coordinator lanes.
4. Review the stable diff and preserve the baseline. Only after that gate, plan
   one separately authorized, rebuilt small original-process characterization
   with strict escalated preflights and no automatic retry. A new first edge
   requires its own RCA; live-256 and the full GUI matrix are not this next gate.

Required controls, without claiming they have been run:

- Exact C#/PowerShell/native token-domain agreement; original long transfer IDs
  that genuinely redact to `[redacted]:1`; normal and repeated redaction retain
  only the projection, not the original long ID or unrelated secrets.
- All five emitter sources; null/sentinel inputs; no current/wake/receipt
  substitution and unchanged generation/raw fields.
- Legacy literal, projected redacted, projected unredacted agreement and mixed
  representations. Token-looking raw inputs are rehashed, not trusted directly.
- Missing fields, malformed token domain/case/length, sentinel values, raw/token
  conflict, same/conflicting duplicate keys and case variants, CR/LF/delimiter
  injection, unsupported partial redaction and no fallback from invalid new mode.
- Required wake mismatch; generation mismatch independent of identity; same
  writer/pair and same generation but changed data-session token must invalidate
  a watched episode. Removing both identity fields from a registered event fails.
- Unknown watched names with identity fields are checked; watched names without
  those optional fields retain behavior. Named terminal/recreated/removed and
  generic pause invalidators reject regardless of identity mismatch or absence.
- Valid foreign writer/pair remains foreign; it cannot rescue an owned mismatch.
  Relevant opaque identity duplicates reject while unrelated sender raw
  duplicates and original coordinator/marker handling remain unchanged.
- Use the real original native custody/receipt path for positive integration
  evidence. Synthetic mutated rows are negative-only controls, not a way to
  manufacture a successful native witness or retroactively repair saved input.
- Preserve resolver/evaluation counts, exact source/receipt/physical identity,
  64-witness/work limits, ten report hashes, primary verdict, all three false
  authority flags, and handle/text cleanup on rejection.

## Preservation and handoff

Entry verification matched all 3,131 previous pins and all 528 dirty file
contents/statuses. Only this document is a new non-ignored file (529 dirty).
Review artifacts and the final preservation seal are under
`artifacts/frontier-data-session-scope-contract-20260928/`.

Next action when directed: implement this bounded diagnostic data-session
identity contract and its focused controls. This review does not itself
authorize or perform another transfer or change acceptance.
