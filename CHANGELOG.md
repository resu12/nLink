# Changelog

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
