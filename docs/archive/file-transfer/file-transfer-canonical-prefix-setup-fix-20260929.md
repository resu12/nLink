# Canonical prefix setup: bounded fixture correction

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus preserved worktree changes.
Status: **test-only correction implemented; 139 distinct affected tests passed.
Native process-pair validation remains outstanding.**

## Reviewed boundary and implementation

The previous single capture failed in the sender prefix-scope constructor, before
payload release. The new outbound-context regression reproduced that exact
`canonical_prefix_scope_retired` exception before the correction.

The correction separates pipe setup from strict witness consumption:

| Phase | Sender peer pause | User pause / receiver peer pause |
| --- | --- | --- |
| Scope construction, pipe setup and start of pending record read | Does not by itself retire an otherwise-current sender scope. | Rejected. |
| Receiver prefix readiness and export | No sender-side readiness is inferred. | Rejected; exact committed-prefix predicate unchanged. |
| Sender record consumption after EOF | Rejected. | Rejected. |
| First actual tail-read gate after the witness | Rechecked and rejected. | Rejected. |

Only `AttemptCanonicalPrefixScope.cs`, `AttemptCanonicalPrefixHandoff.cs` and
`AttemptCanonicalPrefixScopeTests.cs` changed. The scope keeps all original
context, data-session/generation, lifetime, identity, coordinator owner/publication,
retained peer, terminal and geometry checks. Its new setup method permits only
sender peer pause; it does not clear that flag or inspect pause-reason strings.
Pause rejection now has the distinct diagnostic `canonical_prefix_scope_paused`.

The handoff uses the setup check only to establish its pending receive. EOF
completion still performs the strict check. The unchanged profile binding calls
the strict handoff check again immediately before permitting the first tail read.
The receiver's strict readiness/export path is unchanged. Default deterministic
handoff callers retain the original strict behavior.

No wait-for-unpause was inserted before pipe setup. This matters because setup
must not obstruct independently arriving ordinary feedback. Source review confirms
the existing carrier reader is independent
(`Support/AttemptNknPipeClient.cs:59`, `:108`), accepted receiver state updates
`PeerPaused` (`SessionFileTransferService.PullTransferSessionV6.cs:536`), and the
normal sender pump still waits while peer-paused (`:2864`). The V4 batch-selection
guard also still rejects peer pause (`SessionFileTransferService.PullTransferSessionV4.cs:18863`).
No production feedback, resume, route or runtime operation was added.

## Verification

| Evidence | Result |
| --- | --- |
| `bootstrap-red.trx` | One expected failure in the old constructor, matching the native failure. |
| `scope-green.trx` | 31 passed: outbound setup regression, 11 invalid outbound-scope controls, eight asynchronous consumption controls, 11 existing inbound controls. |
| `consumption-final.trx` | Eight consumption cases passed again after strengthening exact error/timeout assertions; these are included in the 31 above. |
| `canonical-regression.trx` | 108 passed in the directly affected prefix-barrier and canonical-profile classes. |

Total latest distinct passing tests: **139**, no failures or skips. The regression
lane includes exact prefix shape, framing/EOF, disposal, cached-tail bypass,
retained-reader behavior, three prefix batch alignments and existing hold-expiry
controls. No broad aggregate or unrelated authority lane was rerun.

The new pipe tests use a real asynchronous local named pipe and the real scope
and profile guards. An independent task takes the actual service gate while the
record read and tail gate are pending. Its state mutations simulate ordinary
feedback in a unit test; they are not a production receiver-state integration
test. Controls reject a still-paused sender at EOF, user pause, re-pause after EOF,
retired owner/peer, context cancellation and disposal without releasing the source
gate. The old inbound tests now verify setup and readiness reject invalid scopes
as well as the strict currentness check.

The first build command did not reach test execution: the sandbox denied
Avalonia's telemetry log under AppData. Subsequent builds used the command-local
`-p:UsedAvaloniaProducts=` setting to skip that telemetry task, as verified against
the installed package targets. No project setting was edited. Compilation
succeeded; existing analyzer/platform warnings remain, so this is not a claim of
a warning-free rebuild. Logs retain both the infrastructure failure and the
deliberate red test.

## Preservation and remaining gate

Entry verification matched 4,544 pinned files and 570 dirty entries. The post-code
audit found only the three intended test source changes and the rebuilt test DLL;
all other prior pins matched, including Core/Infra binaries, production sources,
the native carrier/read factory, receiver readiness/progress, scheduler and
original timing/acceptance artifacts. This report is the only new nonignored file.
No commit or push was made.

No native capture, live NKN, desktop GUI or full matrix ran. No acceptance rule,
runtime authority, payload geometry, hold start/expiry or lifetime changed. The
four-second hold and all original deadlines remain intact.

Evidence and final preservation seal:
`artifacts/canonical-prefix-setup-fix-20260929/`.
The `before/` directory contains line-ending-normalized review copies; original
byte hashes remain in the prior seal and change audit.

Next step: rebuild and perform **one separately gated offline native 256 MiB
canonical capture** with the original preflights and deadlines. It must advance
past scope/pipe setup and establish the exact prefix-before-tail ordering. A
passing unit lane does not prove native timing or same-pair completion. No
automatic retry or full GUI matrix is included.
