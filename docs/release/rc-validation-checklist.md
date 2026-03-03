# RC Validation Checklist

## Build & Packaging

- [ ] CI is green for smoke, reliability gate, packaging, and `installer_smoke`.
- [ ] `artifacts/releases/<version>/` contains `nLink-Portable-win-x64-<version>.zip`.
- [ ] `artifacts/releases/<version>/` contains `nLink-Setup-win-x64-<version>.exe`.
- [ ] `artifacts/releases/<version>/` contains `SHA256SUMS.txt`.

## Install / Uninstall

- [ ] Silent install smoke passed in CI.
- [ ] Manual installer launch works on Windows.
- [ ] Uninstall removes the installed `nLink.exe`.

## Upgrade

- [ ] `0.1.0-beta.5` -> `0.2.0-rc.1` in-place upgrade was tested.
- [ ] Settings or local runtime data were preserved where applicable.

## UI Sanity

- [ ] Session header status text is never empty.
- [ ] Connection pill text matches the allowed set.

## Determinism

- [ ] No `Thread.Sleep` or fixed `Task.Delay` remains in tests, except inside bounded wait helpers.
- [ ] GUI harness has no fixed `Start-Sleep` for SendKeys timing.

## Tag Readiness

- [ ] `VERSION` matches the intended tag.
- [ ] Release notes and changelog are ready.
