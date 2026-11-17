# Late-publication omission diagnostics and exact-scope review

Date: 2026-09-28. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.

## Result

Implemented **test-only original-call omission reporting**. All **42 focused
controls passed**, zero failures/skips. No production resolver, redactor,
acceptance rule, hold policy or runtime deadline changed. No original-process
characterization, live NKN, live-256 or GUI run occurred; no commit or push.

The preceding original run remains failed. Its exact internal rejection was not
recorded and cannot be backfilled from these synthetic controls. The new
reporting is ready for one separately gated diagnostic characterization.

## Bounded implementation

Four existing test/support files changed:

- `FrontierLatePublicationProbeGuards.ps1` appends the existing optional
  `-OmissionReason` reference to the one original resolver invocation. It
  rejects duplicate instrumentation. No second call, evaluation, replay or
  fallback resolver is introduced.
- `FrontierLatePublicationPrimaryWorker.ps1` initializes the diagnostic value
  before running the original primary. `BeforeResolve` resets it for that call.
- `FrontierLatePublicationPrimaryProbe.ps1` emits a single JSON-escaped error
  line when the original witness is null and records `ResolverOmissionReason`
  in its existing final observation. A string is the resolver's reported reason;
  null means no reason was supplied (also inspect `ResolverCalls`); an empty
  string preserves the resolver's empty result and is not independently success.
  Cleanup clears the probe's retained reason after serializing the observation.
- `FrontierLatePublicationGuardTests.cs` extends existing guard controls.
  Original error text, report/view checks, output gating and cleanup remain.

The added diagnostic line is
`late-publication-resolver-omission; reason_json=...`.
It does not change the original missing-witness error or convert rejection into
success. If the resolver itself throws before returning, the original exception
path/cleanup still apply; reporting does not invent an omission reason.

## Focused verification

One Debug build/test invocation, in the normal sandbox:

```powershell
dotnet test tests/nLink.SmokeTests.Core/nLink.SmokeTests.Core.csproj --no-restore -c Debug -p:UsedAvaloniaProducts= --filter 'FullyQualifiedName~FrontierLatePublicationGuardTests.LateWitnessProbe_|FullyQualifiedName~FrontierLatePublicationGuardTests.LateProbe_|FullyQualifiedName~FrontierLatePublicationGuardTests.LateCleanup_|FullyQualifiedName~FrontierLatePublicationGuardTests.LateGuards_' --logger 'trx;LogFileName=focused.trx' --results-directory artifacts/late-publication-omission-diagnostics-20260928
```

- 11 original exact-row witness controls.
- 12 report/state/reason controls, including unreported and quote/CRLF escaping.
- 8 cleanup controls: record reason without changing milestones/verdict, clear it
  alongside retained source/view/payload references.
- 11 instrumentation/flow controls: one optional call, explicit and unreported
  omissions, success, resolver exceptions, probe errors, original report failure,
  no eligible call, unchanged output/disposal, duplicate instrumentation rejection.

The tests exercise the real guard/probe scripts with explicitly synthetic
fixtures, not a new native receipt or authority evaluation.
The Debug build succeeded with the same 338 warnings as the preceding build
after line-number normalization. Scoped diff/whitespace review passed. No
unrelated aggregate suite or transfer lane was rerun.

## Exact-scope review

The saved original input still proves the raw-ID mismatch described in the
previous RCA. The current evidence cannot safely erase that distinction:

- The selected `filetransfer_v6_receiver_state_deferred` row is emitted with raw
  session/transfer fields at
  `SessionFileTransferService.PullTransferSessionV6.cs:6534-6536`. Its persisted
  fields in the failed run are both `[redacted]`.
- The native payload deliberately contains hashed session/transfer identities,
  source graph, repair/lifecycle and completion facts
  (`SessionFileTransferService.FrontierPublicationPayload.cs:72-109`).
- The native manifest authenticates writer/source, payload/observation,
  completion and marker ranges. It does not contain a direct selected-failure
  range-to-observation binding (`LocalFrontierFileManifest.cs:21-57`).
- Original diagnostic-state capture preserves the selected failure/event and
  view identities; it does not supply a missing event-to-transfer link
  (`LocalFrontierDiagnosticState.cs:18-43`).

Same writer, nearby timestamps, matching frontier/route numbers or one currently
available payload are not substitutes for exact session/transfer identity.
A writer can contain multiple transfers. Treating `[redacted]` as a wildcard or
copying receipt IDs into old rows would hide that ambiguity.

The preferred direction for a future reviewed contract is **privacy-safe
per-event scope evidence** using the existing token domain, while leaving raw
IDs redacted. The contract must require exact session AND transfer matching,
retain original writer/physical-source ownership, reject duplicate/conflicting
or incomplete scope, and keep legacy unredacted inputs strict. These are
diagnostic associations only, never route/publication authority.

This is not just a two-field patch to the initial comparison:
`LocalNativeFrontierWitness.ps1:117-118` also partitions the episode by raw IDs,
and later checks depend on scoped lifecycle and invalidation records. A bounded
event/emitter inventory must cover every required/competing record so unscoped
events cannot silently disappear from rejection checks. Redaction tests must
prove raw IDs/secrets remain hidden and the new fixed token shape survives.
Positive, redacted, foreign-transfer, duplicate, missing and ambiguous controls
must exercise the original native source/witness path.

An alternative is a producer-owned native binding for the exact selected row,
but that expands payload/manifest/custody contracts and is not established by
the existing completion/marker ranges. Do not adopt it implicitly. No new
diagnostic field or native schema is implemented/approved by this review.

## Next gate

Run **one separately gated, rebuilt small offline characterization** with these
diagnostics, fresh pins and a create-new marker to obtain the original first
rejection. No automatic retry or cap increase. Keep the 4s late/3s default hold
policies and all production deadlines unchanged.

Use that result to select the first fix edge. The demonstrated scope mismatch
does not exclude an earlier lifetime, input-shape or work-budget rejection.
A generic/unreported reason must remain uncertain. Do not infer original native
success from the deterministic guard tests or relax primary acceptance.

Any scope-binding implementation follows a finite reviewed event contract and
focused regression controls. No live-256 or full GUI matrix is warranted yet.

## Preservation

Entry matched 2,119 saved pins and 514 dirty entries. Three test-support scripts,
one existing test source and the rebuilt test DLL are the only intentional
prior-pin replacements; exact before-images are saved. The other 2,114 pins,
all unrelated dirty contents/statuses and the failed original run are unchanged.
This handoff is the only new non-ignored file (515 dirty entries total).
Evidence and the independent final audit are under
`artifacts/late-publication-omission-diagnostics-20260928/`.
