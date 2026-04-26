# RC Validation Checklist

## Build & Packaging

- [ ] CI is green for smoke, reliability gate, packaging, and `installer_smoke`.
- [ ] `tools\Test-Lanes.ps1 -Lane Smoke -Configuration Release` passes, or the CI smoke lane passed from the same commit.
- [ ] Any changed domain has its ownership lane green: `Core`, `Gui`, `ScreenShare`, `RemoteControl`, or `Contracts`.
- [ ] `artifacts/releases/<version>/` contains `nLink-Portable-win-x64-<version>.zip`.
- [ ] `artifacts/releases/<version>/` contains `nLink-Setup-win-x64-<version>.exe`.
- [ ] `artifacts/releases/<version>/` contains `SHA256SUMS.txt`.

## Install / Uninstall

- [ ] Silent install smoke passed in CI.
- [ ] Manual installer launch works on Windows.
- [ ] Uninstall removes the installed `nLink.exe`.

## Upgrade

- [ ] Previous supported public installer -> current RC in-place upgrade was tested.
- [ ] Settings or local runtime data were preserved where applicable.

## UI Sanity

- [ ] Session header status text is never empty.
- [ ] Connection pill text matches the allowed set.

## Determinism

- [ ] No `Thread.Sleep` or fixed `Task.Delay` remains in tests, except inside bounded wait helpers.
- [ ] GUI harness has no fixed `Start-Sleep` for SendKeys timing.

## Tag Readiness

- [ ] `VERSION` matches the intended tag.
- [ ] Release notes are ready under `docs/releases/<version>.md`.
- [ ] Support guidance still points to `docs/supportability.md` and Diagnostics / Hang Report capture.
