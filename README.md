# nLink

nLink is a private, account-free Windows x64 app for helping family and friends. It uses [NKN](https://nkn.org/) for chat, screen sharing, optional remote control, and file transfer.

## Download 0.7.1

- [Windows x64 installer (recommended)](https://github.com/resu12/nLink/releases/download/v0.7.1/nLink-Setup-win-x64-0.7.1.exe)
- [Windows x64 portable ZIP](https://github.com/resu12/nLink/releases/download/v0.7.1/nLink-Portable-win-x64-0.7.1.zip)
- [SHA256SUMS.txt](https://github.com/resu12/nLink/releases/download/v0.7.1/SHA256SUMS.txt)
- [0.7.1 release notes](https://github.com/resu12/nLink/releases/tag/v0.7.1)

## What's new in 0.7.1

Drop a file from Windows Explorer into chat, choose where to save an incoming file, or enable Tuna mid-session without reconnecting the help session.

## Quick Start

1. On the Helper's PC, click **I want to help someone** and share the verified helper address once it's ready.
2. On the Helpee's PC, click **I need help**, enter the helper address, and click **Request help**.
3. The Helper clicks **Accept**. Both people compare the five verification symbols. If they match, the Helpee clicks **Allow**.
4. Use chat, or have the Helpee click **Share screen**. Remote control requires separate approval.

![nLink home screen with help and helper options](docs/images/home-2026-11-16.png)

## Usage notes

- **Files:** When file transfer is allowed, either person can click **Send file** or drop one local file into chat, including while screen sharing. Each offer needs the receiver's approval. Folders, multi-file drops, and resuming after an app restart aren't supported.
- **Saving:** **Accept** saves to Downloads and adds a numbered suffix if the name is taken. **Save as…** lets you choose a location and name without overwriting an existing file. Canceling the picker leaves the offer pending.
- **Tuna:** Starting a paid listener can spend NKN. **Tuna is ready** means the session connection is confirmed. It doesn't tell you which route a file is using. Reconnecting Tuna doesn't resume a paused file or automatically switch its route.
- **Troubleshooting:** Open **Options → Diagnostics**, refresh **Session snapshot**, then select **Copy diagnostics**. [Review the report before sharing it](docs/supportability.md).

## Documentation

[Docs index](docs/README.md) · [Release runbook](docs/ReleaseRunbook.md) · [MIT license](LICENSE)

For maintainers: [test lanes](docs/test-lanes.md) (`tools\Test-Lanes.ps1`), [screenshare evidence](docs/screenshare-operability.md) (`tools\ScreenShare-Ops.ps1`), and the [RC checklist](docs/release/rc-validation-checklist.md).

Created by Codex/GPT.
