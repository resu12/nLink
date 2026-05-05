# Changelog

## [0.7.0] - 2026-05-05

### Added

- Experimental NKN Tuna acceleration research artifacts, sidecar tooling, and opt-in manual benchmark coverage.
- Diagnostics now includes a visible `Tuna (experimental)` wallet-linking section for local wallet status, balance validation, address copy, and unlinking.
- Safe `wallet-status` validation mode in the Tuna sidecar unlocks a linked wallet for one balance check and exits without starting a paid listener or spending NKN.
- Phase 3 benchmark artifacts and acceptance gates compare current NKN and Tuna for file, screen, and reconnect behavior.

### Changed

- Release version sources and packaging defaults now resolve to `0.7.0`.
- README and release-facing documentation now reflect `0.7.0` as the current release.
- Tuna remains default-off and developer-gated; a linked or funded wallet does not change runtime transport behavior by itself.
- Diagnostics copy/export redaction now covers Tuna wallet paths, wallet addresses, password-like fields, seeds, and private-key material.

### Fixed

- Tuna negotiation is bound to the approved, verified nLink session and silently falls back to the current NKN transport on address, session, nonce, version, expiry, lane, or sidecar mismatches.
- Sidecar and benchmark failure handling was tightened so disconnects and listener shutdowns mark acceleration unavailable without ending the approved nLink session.

### Packaging

- Windows installer and portable release assets for `0.7.0` are prepared together with `SHA256SUMS.txt`.
- The Tuna sidecar verifier is bundled under `tuna/win-x64/` so Diagnostics wallet validation works in packaged builds while runtime acceleration remains default-off.
- Public Windows artifacts are unsigned for this release as an accepted exception.

## [0.6.2] - 2026-05-01

### Added

- Final `0.6.2` release notes under `docs/releases/`.
- V4-only file-transfer pause and resume controls for active inbound and outbound transfers, with cancel still available while paused.
- Session verification derivation and approval UI using a compact emoji sequence plus fallback code so helper and helpee can compare the same handshake-derived value before allowing a session.
- File-transfer operator docs, soak tooling, and stabilization gates for V4 transfer validation.
- Receiver-confirmed file-transfer progress so the sender UI reflects accepted transfer progress instead of only locally queued bytes.
- Advanced Diagnostics screen-share presets for Balanced, High quality, and High performance.

### Changed

- Release version sources and packaging defaults now resolve to `0.6.2`.
- README and release-facing documentation now reflect `0.6.2` as the current release.
- File-transfer protocol and tests now focus on the V4 path after removing legacy V1/V2/V3 transfer frames and pull-session tests.
- Received files now default to the Windows Downloads folder and automatically receive a numbered suffix when the original file name already exists.
- Completed file-transfer pills can open the destination folder, and longer transfer status messages are displayed without being clipped.
- Helper and helpee session verification presentation was tightened and made more compact and uniform.
- Helper first-run and request screens keep a stable width while the helper address loads.
- Diagnostics now labels the release transport as `NKN internet transport`.

### Fixed

- Help-request accept/decline handling was hardened so stale or closed peers during the approval phase recover to the correct starting state.
- Helper request admission and NKN bridge startup paths now reject or recover from stale pending requests more reliably.
- Helpee chat entry and send behavior during screen sharing was restored.
- The Windows taskbar/app icon is applied instead of the blank default app icon.
- Transient NKN bridge bulk sends are retried to improve file-transfer delivery without changing the stable production fanout defaults.
- V4 file-transfer repair delivery and receiver-side progress behavior were tightened while preserving mixed-transfer pacing defaults.
- High quality screen-share preset delivery now aligns with the transport pipeline FPS cap.

### Packaging

- Windows installer and portable release assets for `0.6.2` are prepared together with `SHA256SUMS.txt`.
- Public Windows artifacts are unsigned for this release as an accepted exception.

## [0.6.0] - 2026-04-26

### Added

- Final `0.6.0` release notes under `docs/releases/`.
- H.264 screenshare transport as the default screen-sharing path, replacing the legacy JPEG frame-update model for normal screen sharing.
- Helper-side cursor overlay telemetry so cursor motion can remain smooth even when video frames briefly hold.
- Screenshare operator and Diagnostics evidence for H.264 visual integrity, cursor delivery, WGC GPU scaling, CPU cadence, packaging size, and WGC teardown state.
- Explicit helper address regeneration for privacy resets while keeping the normal helper address stable across restarts.
- Packaging cleanup and size-reporting tools, including download-size versus installed-size packaging modes.

### Changed

- Release version sources and packaging defaults now resolve to `0.6.0`.
- README and release-facing documentation now reflect `0.6.0` as the current release.
- Screenshare capture now uses upstream raw-capture cadence, WGC GPU scaling, and same-size direct preprocessing to lower helpee CPU cost without reducing the normal quality target.
- Helper screenshare presentation now uses high-quality interpolation for meaningful upscaling and downscaling.
- Diagnostics was reorganized around support-first connection, identity, and screen-share health details, with noisier counters moved into advanced diagnostics or copied reports.
- Default packaging is optimized again for smaller download artifacts while keeping installed-size optimization available as an explicit build option.

### Fixed

- H.264 recovery, keyframe/IDR motion handling, reference-chain quarantine, and helper visual gating reduce broken text bands and corrupted inter-frame artifacts during scrolling and fast motion.
- Helper-side cursor overlay reduces captured-cursor tail and improves perceived cursor fluency.
- Helper reduced/catch-up recovery and visible-progress accounting were tightened so healthy periods can return to normal screenshare mode more reliably.
- Win10 Windows Graphics Capture teardown now closes WGC lifecycle objects on the owning apartment to prevent the yellow capture border from lingering after sharing stops.
- Helper closure and remote-end paths were hardened so helpee sharing stops immediately and late capture frames or queued restarts cannot revive sharing.
- Failed/no-session screenshare soaks are classified as setup failures instead of being mistaken for quality evidence.

### Packaging

- Windows installer and portable release assets for `0.6.0` are prepared together with `SHA256SUMS.txt`.

## [0.5.3] - 2026-03-25

### Added

- Final `0.5.3` release notes under `docs/releases/`.
- Helper-ID-first direct request flow with helper-side incoming help request acceptance.
- Helpee QR import menu with both file import and camera scan actions.

### Changed

- Release version sources and packaging defaults now resolve to `0.5.3`.
- README and release-facing documentation now reflect `0.5.3` as the current release.
- Helper and helpee connection flow now centers on sharing a helper address and sending a direct help request instead of a manual invite-return handoff.
- Chat input now sends on `Enter` and inserts a new line with `Shift+Enter`.
- Chat shell sizing was stabilized for chat-only and screen-sharing layouts, and the side-by-side screen-sharing chat pane was narrowed to leave more room for the shared screen.
- Helper waiting/share layout was tightened so the helper address card no longer grows wider or leaves unnecessary footer space as content loads.
- NKN file transfer now starts with a more conservative V3 startup profile to reduce repair churn on slower links.

### Fixed

- Repeated-session helper and helpee lifecycle handling after reject, timeout, remote end, and local end now returns to the correct waiting screens more reliably.
- Helpee `Request help` recovery was fixed after reject/end flows and when the authoritative local NKN address is temporarily suppressed during invite preparation.
- Stale chat text, peer-ended notices, and other previous-session presentation state no longer persist into new sessions as often.
- Late duplicate NKN handshake failures no longer invalidate the active approved helpee session and disable chat/share controls after several sessions.
- Helper passive approval-timeout recovery was hardened to return to `Waiting for help requests…` instead of leaving the helper on a blank `Connection failed` shell.

### Packaging

- Windows installer and portable release assets for `0.5.3` are prepared together with `SHA256SUMS.txt`.

## [0.5.2] - 2026-03-23

### Changed

- Release version sources and packaging defaults now resolve to `0.5.2`.
- Improved native file transfer with metadata-first startup and newer V3 streaming for updated peers
- Better transfer throughput with larger chunks, larger healthy in-flight windows, and reduced control chatter
- Reduced inbound file-transfer head-of-line blocking
- Fixed helper identity bootstrap so helper-bound invites match the real connected helper again
- Added safer recovery for unreadable protected per-process seed storage
- Added startup cleanup for stale identity.instance-* identity files from dead processes
- Removed helper recent-address history
- File size cap is 1 GiB

## [0.5.1] - 2026-03-13

### Added

- Final `0.5.1` release notes under `docs/releases/`.
- Persistence diagnostics and warning reporting for secret-store, identity-store, and diagnostics-export failures.
- Explicit diagnostics privacy messaging that redaction is best-effort and should be reviewed before sharing.

### Changed

- README and release-facing documentation now reflect `0.5.1` as the current release.
- Release version sources and packaging defaults now resolve to `0.5.1`.
- Chat now uses the session-secure transport envelope path.
- Protected secret storage is now the intended seed-storage path across supported desktop platforms; plaintext fallback is no longer part of the normal release model.
- The local .NET <-> bridge hot path now uses a framed binary stdio protocol instead of JSON `payloadBase64` for hot send/receive traffic.
- Privileged session actions now route through a stricter internal authorization boundary.

### Fixed

- File transfer was hardened for transport-aware pause/resume, reconnect handling, NKN bulk dispatch behavior, and mixed-session recovery after the initial `0.5.0` rollout.
- Helpee invite preparation no longer stays stuck in `TransportInitializing` when protected seed loading fails during startup.
- Runtime and transport responsibilities were split into narrower internal components to reduce audit and regression risk.

### Packaging

- Installer and portable naming defaults now target `0.5.1` release assets.

## [0.5.0] - 2026-03-10

### Added

- Final `0.5.0` release notes under `docs/releases/`.
- Native in-session single-file transfer for helper and helpee, protected by the dedicated `Transfer files` capability.
- File-transfer diagnostics, runtime summary fields, and troubleshooting logs for transfer state and failure reasons.

### Changed

- README and release-facing documentation now reflect `0.5.0` as the current release.
- File transfer now uses the authenticated active session model instead of the previous external-tool handoff.
- Installer references now point to `%LOCALAPPDATA%\Programs\nLink`.

### Fixed

- File transfer is now session-bound, capability-gated, and protected with end-to-end encrypted payload handling on both DevLocal and NKN transports.
- Receiver-side file save flow now uses temp-first assembly, whole-file integrity verification, safe finalize, and overwrite-blocked defaults.
- File-transfer UI now shows accept/decline, progress, cancel, success, and failure states inside the shared session/chat pane.
- NKN file-transfer handling was hardened for chunk ordering, replay-window sizing, transport-aware payload budgeting, and large-file reliability.
- Diagnostics and logs now expose actionable file-transfer failure details without logging file contents.

### Packaging

- Windows installer and portable release assets for `0.5.0` are prepared together with `SHA256SUMS.txt`.

## [0.4.5] - 2026-03-08

### Added

- Final `0.4.5` release notes under `docs/releases/`.
- Screen-share metrics for capture/send/render timing, pacing, drops, stale-frame detection, and byte accounting.
- Release diagnostics for invite-security posture, risky overrides, and transport hardening counters.
- Helper-side share action for the helper address, alongside copy.

### Changed

- README, screenshots, and release-facing documentation now reflect `0.4.5` as the current release.
- `0.4.5` is now documented as the cumulative release since `0.4.2`.
- Screen sharing now preserves more readable UI text by default and reacts to transport pressure before wasting bandwidth.
- Helper/helpee waiting flows were simplified around sharing a helper address first and a helper-bound invite second.
- Release-default invite flow now uses helper-bound issued-secret invites instead of the older shared-secret default.

### Fixed

- Screen-share pacing, payload sizing, and viewer apply timing are tighter and better instrumented.
- Screen-share send/receive paths now discard stale work more aggressively under unstable links and after display mapping changes.
- Screen-share soak and performance coverage now include capture-to-render timing and resize/display-change recovery budgets.
- Post-handshake transport protection and replay resistance now cover remote control, lifecycle traffic, and screen share, not just chat.
- Plaintext local NKN seed storage was removed from the normal Windows path in favor of protected local storage.
- Release hardening now includes stronger queue bounds, overflow diagnostics, and release-mode guardrails for security-relevant overrides.

### Packaging

- Windows installer and portable release assets for `0.4.5` are prepared together with `SHA256SUMS.txt`.

## [0.4.1] - 2026-03-07

### Added

- Final `0.4.1` release notes under `docs/releases/`.

### Changed

- README, screenshots, and release-facing documentation now reflect `0.4.1` as the current release.
- `0.4.1` is documented as a stabilization release for the remote-control, session-state, and installer hardening work built on `0.4.0`.

### Fixed

- Remote-control request/mapping recovery, disconnect cleanup, helper/helpee stale-state presentation, and bridge shutdown/packaging regressions.

### Packaging

- Windows installer and portable release assets for `0.4.1` are verified together with `SHA256SUMS.txt`.

## [0.3.1] - 2026-03-03

### Added

- Final `0.3.1` release notes under `docs/releases/`.

### Changed

- README and release runbook now reflect `0.3.1` as the current release.
- `0.3.1` is documented as a stabilization release for the bounded screensharing pipeline introduced in `0.3.0`.

### Packaging

- Versioned release examples and installer references now point to `0.3.1`.

## [0.3.0] - 2026-03-03

### Added

- Final `0.3.0` release notes under `docs/releases/`.
- Manual screenshare soak harness and release-facing screenshare validation docs.

### Changed

- README and release docs now reflect `0.3.0` as the current release.
- Session shell and header copy were tightened so screenshare state reads more clearly across helper and helpee flows.
- Screenshare delivery is now paced and bounded for stability over the existing bridge/message transport.

### Fixed

- Screenshare start/stop, disconnect, chat coexistence, and end-session cleanup regressions across helper and helpee flows.
- Helper and helpee status presentation now avoids duplicate transient status chrome and incorrect failure copy for remote-ended sessions.
- GUI/input timing and screenshare viewer state transitions were hardened with deterministic waits and regression coverage.

### CI

- Added a short Release performance gate for bounded screenshare pressure handling.
- Release validation now covers screenshare coexistence, packaging verification, and GUI smoke regressions.

### Packaging

- Windows installer and portable release assets for `0.3.0` are verified together with `SHA256SUMS.txt`.

## [0.2.0] - 2026-03-03

### Added

- Release validation checklist for build, install, upgrade, UI sanity, and tag readiness.
- Final `0.2.0` release notes draft under `docs/releases/`.

### Changed

- Session header and chat connection pill copy now use consistent `Connecting…` and `Reconnecting…` text.
- README now calls out the `0.2.0` release scope and current installer path.

### Fixed

- Windows screen-capture lifecycle smoke test no longer relies on fixed delays to detect settled logging.
- GUI smoke SendKeys input path no longer relies on fixed sleeps for text replacement or Enter send timing.

### CI

- CI builds installer and portable artifacts, verifies release assets, and uploads packaging diagnostics.
- CI adds a silent Windows installer install/uninstall smoke job.

### Packaging

- Release assets are verified against `VERSION`, including the portable ZIP, installer EXE, and `SHA256SUMS.txt`.
- Packaging diagnostics are uploaded even when installer packaging fails.
