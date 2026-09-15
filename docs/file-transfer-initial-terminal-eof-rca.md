# C2l: initial listener EOF handoff and legacy-child module environment

2026-09-15. Baseline checkpoint: `2fa9c0277b11fedeef2faae62ae57fdb7b832ae3`.
This is a bounded offline follow-up to C2k. No additional live run, primary
authority evaluation, historical evidence rewrite, push or full matrix is included.

## Reporting failure

The owned-relay launcher uses `ProcessStartInfo` to start Windows PowerShell.
From PS7 it inherits PS7's module search path, unlike the original native
`powershell` invocation. The current live wrapper therefore could not resolve
`Get-FileHash` while writing its direct evidence manifest.

The new real-launch regression reproduces this exact error before the fix:
PS7-to-PS5 fails; PS5-to-PS5 passes. It exercises native baseline, genuine relay
adoption and nonzero-wrapper exit, and requires an independently verified SHA-256
of `VERSION`, the Windows PowerShell Utility module, preserved output ordering
and unchanged parent environment.

The fix removes `PSModulePath` only from the owned legacy child's environment
when the parent is PowerShell Core. Windows PowerShell initializes its configured
defaults. The same-host PS5 and original native branches remain unchanged.
This does not emulate every custom process-only module-path setting; this wrapper
needs the installed standard modules, not a custom injected module path. Native
PowerShell's filtering implementation is internal, so the fix does not call that
private API or copy its algorithm. See the primary
[PowerShell implementation](https://raw.githubusercontent.com/PowerShell/PowerShell/v7.6.0/src/System.Management.Automation/engine/Modules/ModuleIntrinsics.cs).

No manifest is regenerated for C2k. Its nonzero GUI exit independently prevents
primary retained analysis and remains a failure even with this reporting repair.

## First runtime proof edge

Only the current C2k source ledger is used:
`artifacts/publication-checkpoint-live-20260915/runs/20260915T102827Z/regular-v4-live-activation-off-on-off-256mb/filetransfer-gui-log-ledger-slice.log`.

| Source line | Sender sequence | Observation |
| --- | --- | --- |
| 26798 | 18347 | Exact initial Regular-v4-to-Tuna-v4 transition pause installed |
| 26806 | 18351 | Coordinator transition Draining |
| 26845 | 18386 | Started precommit probe submitted through Tuna |
| 27236 | 18539 | Unavailable reason `listener_terminal_tuna_stream_eof` |
| 27238 | 18541 | Transport pause release rejected: Coordinator invalidation required |
| 27246 | 18549 | Same transaction/probe failed, no ACK or route commit |
| 27250 | 18553 | Probe-sidecar failure names the listener terminal EOF |
| 50762 | 31221 | Transition invalidated only at outbound terminal/deadline |

There is no initial-activation probe-failure observation or Tuna publication in
the source. Writer custody/drains passed in the original GUI ledger.

`NknTunaListenerSidecarSupervisor` emits `listener_terminal_` plus the terminal
reason on `tuna_bridge_terminal`. `TryCaptureRuntimeUnlockInitialActivationPreCommitProbeFailure`
first calls a shared listener-rearm reason predicate that does not recognize
`listener_terminal_tuna_stream_eof`. Thus this exact live notification cannot
capture the observation, before any Coordinator/source/probe binding checks.
The callback still resets negotiation and fails the Started probe. A later
recognized close cannot capture a now-terminal probe.

The live callbacks overlap; writer ordering does not establish the exact lock
interleaving of the later `remote_closed` capture. The first notification's
reason rejection is established independently. The network cause of the EOF is
not established and is outside this fix.

## Bounded implementation and verification plan

1. Reuse the attached-service deterministic fixture with the exact listener
   reason followed by `remote_closed`. Require one original observation,
   Coordinator-owned invalidation/rollback, unchanged original route/runtime
   ownership, resumed source session and no Tuna publication or retry install.
2. Reproduce failure before production edits. Extend the existing malformed and
   real lifecycle controls to the new reason, including disposal, replacement,
   generation ABA, ACK, conflicting raw transfer/lease and installed-retry mode.
3. Recognize only this exact additional reason in the initial-activation capture.
   Do not broaden the shared rearm predicate, post-fallback handler, receiver
   path, source-currentness checks or Coordinator authority rules.
4. Run the focused callback regressions and directly affected handoff lane once
   after stabilization. Keep launcher/finalizer verification separate.
5. Before runtime validation, review/checkpoint the bounded changes, rebuild the
   portable package and obtain authorization for the next targeted live-256 run.
   The earlier single-run approval has already been consumed by C2k.

Offline results are recorded separately under
`artifacts/publication-relay-module-path-20260915/RESULTS.md` and
`artifacts/publication-initial-terminal-eof-20260915/RESULTS.md`.

## Completed offline verification

Both fixes are implemented. Final affected results: two cross-host hash cases,
four existing relay/finalizer cases and 20 initial-activation handoff cases pass.
Each production defect has an exact pre-fix RED reproduction and post-fix GREEN
verification. No Coordinator rule or existing stale/currentness predicate changed.

The expanded handoff fixture initially triggered invitation throttling because
all cases reused one fake address pair. The saved log identifies three setup
rejections before the tested path. Unique fake identities per invocation corrected
the fixture; production throttling and test/runtime timeouts were not relaxed.

Pre-commit inspection found no additional actionable issue in these bounded
changes. The live transfer remains unverified after the fixes; the cause of the
original EOF is still open. Stop before a new checkpoint/build/live-256 sequence
until the user authorizes that next gate. No previous artifact is backfilled.

## C2m launch authorization

The user subsequently authorized the reviewed local checkpoint, portable rebuild
and one new targeted live-256 test with strict preflights. This supersedes the
stop-before-live boundary for one attempt only. No push, full matrix, preflight
bypass, historical reclassification or additional runtime change is included.
