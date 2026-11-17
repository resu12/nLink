# Initial fallback first-session binding implementation

## Scope and outcome

This implements prerequisite 1 of `file-transfer-initial-fallback-startup-20260923.md`.
It does not yet accept the initial fallback owner-successor route contract.
The baseline remains `0a2cd9d37dbd81149e367f6ce8729d0e0f7e82a0` on `v0.7.0`;
all changes are local and uncommitted.

The coordinator now records the first fallback data-session attachment before
startup can replace its owner. Fresh production-service loopback evidence has
an exact publication → binding → startup aggregate-generation/hash/scope chain
in both directions. The old unexplained `(none)`/0 → identity/1 jump is gone.

The analyzer still reports the two expected `snapshot same-route publication
has no exact fallback owner successor contract` findings. Snapshot artifact
evidence, stream completeness, and full/compact parity pass. This is not live
route acceptance, and the preceding live scenario remains failed.

## Implementation

- `FileTransferCoordinator.InitialFallbackDataSession.cs` defines a typed
  request, binding, decision, and snapshot witness. The request is tied to the
  coordinator instance, session, transfer, direction, exact current runtime
  owner, and exact first publication.
- Acceptance is limited to a nonterminal initial fallback V6/epoch-zero active
  leg with no data session and no conflicting checkpoint, control-plane, repair,
  transition, recovery, admission, precommit, or retry-invalidation scope.
  The only data-binding change is generation 0 to 1. Existing owner, leg,
  publication, and unrelated persistent authority remain unchanged.
- Rejections return the same state, no binding, and no state-mutating envelope.
  Duplicates and later bindings cannot use this coordinator method to replace a
  session. The service's already-attached same-object check remains a no-op.
- The inbound/outbound service branches apply the accepted session and generation
  with its coordinator-authored persistent snapshot state under the existing
  lock, before subscribing callbacks. A rejected initial request cannot fall
  through to the legacy replacement path. Later-session and transitioned-route
  replacement behavior is unchanged.
- The production emitter projects the typed source/effective boundaries and
  owner/publication fingerprints. The marker grants no route-publication or
  runtime-unlock authority and sends no frames.

## Snapshot compatibility

The new pinned catalog suffix is `-initial-data-session-v1`, hash
`90cf0fdafc50d49c866bbb9bdda9097b3306ae5a7fd9d1198b03c02c66ef18ab`.
Its parent hash stays
`17196929ff74bd92c4866ccb8da33de05eb5b22d5436f56488f3a7fd2f4d086b`.

`InitialDataSessionEvidence.ps1` validates the exact marker shape, accepted
receipt, generation/hash transition, initial fallback scope, and preserved
authority generations. Existing family validators explicitly recognize the
new child catalog without changing their validation rules. Redaction permits
only the registered catalog/profile suffix and still hides raw identifiers.

No initial-successor exemption was added to route consistency. In particular,
the existing `state_refresh_received` transitioned-fallback validator is
unchanged. The next step must connect the binding's owner/publication
fingerprints to the preceding exact publication and validate the successor
retirements, entrants, receipt, and forwarding effect; mere marker presence is
not a complete route proof.

## Verification

Artifacts are under `artifacts/initial-fallback-binding-20260923`.

- Core tests cover both directions and independent runtime/leg generation
  domains; 35 invalid-scope/request controls per direction, plus duplicate and
  later-generation rejection checks, preserve the input state exactly.
- Service tests use contexts created through ordinary send/offer admission.
  Foreign and terminal attachments do not assign a session, subscribe callbacks,
  send frames, dispose the supplied session, or change coordinator state.
  Successful attachment is already applied when the first subscription occurs;
  a repeated same-object attachment does not resubscribe or reissue evidence.
- The fresh 128,000-byte production-service loopback completes with exact
  received bytes. Both PowerShell hosts verify the recorded chain and retain
  the separate owner-successor route failure. Each host rejects 48 malformed
  binding controls across the two directions.
- A temporary verification failure exposed the new catalog being redacted as
  unknown. The exact-profile redactor was extended and the focused tests passed;
  the failed TRX and original output are retained, not overwritten as a pass.
- The directly affected compatibility lane covers initial fallback publication,
  initial Tuna/fallback exchange, checkpoint republication, catalog profiles,
  and evidence redaction. Final counts and SHA-256 pins are recorded in
  `FINAL-VERIFICATION.json` by `Verify-Implementation.ps1`.
- Final stabilized verification: 67 affected-contract cases plus 2 fresh
  end-to-end evidence cases, 69 unique cases passed and none failed. All 139
  prior live pins still match. Earlier intermediate TRX results are retained
  separately and are not added to this final count.

Build/test commands use `--no-restore` and the command-local
`-p:UsedAvaloniaProducts=` switch to avoid the external Avalonia telemetry log
write in the restricted sandbox. No project or machine telemetry configuration
was changed.

No portable rebuild, live GUI/network run, historical authority re-analysis,
full matrix, commit, or push was performed in this step.

## Next step

Implement prerequisite 2: the exact initial-fallback startup successor reducer,
using this binding as a required contiguous proof edge. Keep all invalid/stale,
wrong-route/protocol, missing-history, and duplicate-effect controls fail-closed.
Only after that contract and its focused regressions pass should the targeted
live validation gate be prepared.
