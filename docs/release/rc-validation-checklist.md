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

## Security Gates

- [ ] Release shell is clean of `NLINK_UNSAFE_DEVELOPER_MODE`, `NLINK_TRANSPORT=DEVLOCAL`, `NLINK_NKN_*`, `NLINK_FILETRANSFER_*`, `NLINK_SCREENSHARE_UNSAFE_*`, `NLINK_NKN_NODE_PATH`, `NLINK_NKN_BRIDGE_PATH`, `NLINK_DOWNLOAD_URL`, and `NLINK_REPO_URL` override env vars before packaging.
- [ ] Diagnostics from the packaged app show no unexpected `security_relevant_overrides`; any `release_override_suppressed` evidence is investigated before sign-off.
- [ ] File transfer is validated as shipped scope only: V4-only, single-file, explicit accept/decline, session-envelope protected, and source/session validated.
- [ ] Live NKN file-transfer soak passed on the packaged app with integrity OK and no `filetransfer_data_session_overflow`, `filetransfer_message_rejected`, or bridge stdout protocol-violation events.
- [ ] Any `post_completion_late_frame_ignored_count` evidence is reviewed as benign authenticated NKN late delivery after terminal completion, not as a protocol reject.
- [ ] Packaged app uses bundled `bridge/win-x64/node.exe` and `bridge/win-x64/index.js`; public release does not depend on bridge path overrides.
- [ ] Release evidence confirms file-transfer queue limits (`512` frames / `32 MiB`) and bridge binary caps (`64 KiB` payload, `196,606` body bytes before allocation).

## Determinism

- [ ] No `Thread.Sleep` or fixed `Task.Delay` remains in tests, except inside bounded wait helpers.
- [ ] GUI harness has no fixed `Start-Sleep` for SendKeys timing.

## Tag Readiness

- [ ] `VERSION` matches the intended tag.
- [ ] Release notes are ready under `docs/releases/<version>.md`.
- [ ] Support guidance still points to `docs/supportability.md` and Diagnostics / Hang Report capture.
