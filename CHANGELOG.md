# Changelog

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
