# Changelog

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
